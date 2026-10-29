/**
 * @file session.cpp
 * @brief Session lifecycle behind the C API: configuration validation, the deep copy, the
 *        coordinator thread that prepares, runs and releases a test, the time-remaining
 *        estimate and the handle registry.
 *
 * Shared unchanged by ramquick_engine.dll (through api.cpp) and ramquick_engine_test.dll
 * (through test_exports.cpp), so both binaries have one validation, one stop-reason record and
 * one registry. Contracts are documented in core/session.h and ramquick_engine.h.
 *
 * @par Coordinator
 * One thread per session sequences everything and raises every callback; workers never call
 * back, so callbacks never run concurrently. Its order is: stop-before-start check, preparation
 * (worker placements, error storage, the locked region, the pinned pool, the step runner),
 * on_prepared, then each step as gate, run_step and on_step_finished, then the frozen end
 * reason. The tail always runs, for every end reason: join the workers, deliver the last error
 * records, store the result, on_completed, release the region chunk by chunk, on_released.
 *
 * @par Exceptions
 * A C++ exception anywhere in preparation or the steps becomes RQ_END_INTERNAL_FAULT with its
 * what() text, and the tail still runs. SEH faults are deliberately neither caught nor
 * translated (no __try, no SE translator, no vectored handler): under unstable memory such a
 * fault may itself be a symptom, so the process terminates and the host reports the unfinished
 * run through journal recovery at its next launch.
 */
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <exception>
#include <iterator>
#include <memory>
#include <mutex>
#include <span>
#include <stdexcept>
#include <system_error>
#include <thread>
#include <unordered_set>
#include <utility>
#include <vector>

#include "core/session.h"
#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/kernels/kernels.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_access.h"
#include "core/memory/real_memory_access.h"
#include "core/memory/memory_region.h"
#include "core/errors/error_collector.h"
#include "core/runner/worker_pool.h"
#include "core/runner/step_runner.h"
#include "core/runner/coverage_counter.h"

/**
 * The object behind an rq_session_handle. Defined in the global namespace because
 * ramquick_engine.h declares the handle as struct rq_session_t*.
 *
 * It only holds the session's shared ownership, so a registry lookup can hand out a
 * std::shared_ptr that keeps the session alive for one call even while another thread destroys
 * the handle.
 */
struct rq_session_t final {
    std::shared_ptr<rq::engine::session> impl;
};

namespace rq::engine {

// validate_config's per-worker minimum is what lets every shrink-and-retry attempt plan a
// non-empty slice for each worker, so it must stay the region's slice granularity.
static_assert(k_min_slice_bytes == k_slice_granularity_bytes,
              "the validated per-worker minimum must equal the slice granularity");

namespace {

constexpr std::uint64_t k_ns_per_ms = 1'000'000;

/** Nanoseconds from then to now; 0 if the clock reading did not advance. */
constexpr std::uint64_t elapsed_since(std::uint64_t now, std::uint64_t then) noexcept {
    return now > then ? now - then : 0;
}

constexpr std::uint64_t saturating_add(std::uint64_t a, std::uint64_t b) noexcept {
    return a > UINT64_MAX - b ? UINT64_MAX : a + b;
}

constexpr std::uint64_t saturating_mul(std::uint64_t a, std::uint64_t b) noexcept {
    return (a != 0 && b > UINT64_MAX / a) ? UINT64_MAX : a * b;
}

/**
 * Writes one line to the debugger. The C API has no warning channel, so the non-fatal
 * conditions the coordinator meets (failed priority changes, an unsignalled stop event) are
 * visible there only. _vsnwprintf_s with _TRUNCATE cuts an overlong line instead of raising the
 * CRT invalid-parameter handler, which would terminate a static-CRT process.
 */
void debug_log(_Printf_format_string_ const wchar_t* format, ...) noexcept {
    static constexpr wchar_t k_prefix[] = L"RAMQuick engine: ";
    constexpr std::size_t k_prefix_length = std::size(k_prefix) - 1;
    wchar_t buffer[256];
    std::wmemcpy(buffer, k_prefix, k_prefix_length);

    // Leaves room for the trailing newline and the terminator.
    wchar_t* const text = buffer + k_prefix_length;
    const std::size_t text_capacity = std::size(buffer) - k_prefix_length - 1;
    va_list args;
    va_start(args, format);
    const int written = _vsnwprintf_s(text, text_capacity, _TRUNCATE, format, args);
    va_end(args);

    const std::size_t length = written >= 0 ? static_cast<std::size_t>(written) : std::wcslen(text);
    text[length] = L'\n';
    text[length + 1] = L'\0';
    OutputDebugStringW(buffer);
}

/**
 * True when text[0, length) is well-formed UTF-8 (Unicode table 3-7): no stray continuation
 * byte, no overlong form, no surrogate code point and nothing above U+10FFFF.
 */
bool is_valid_utf8(const unsigned char* text, std::size_t length) noexcept {
    std::size_t i = 0;
    while (i < length) {
        const unsigned char lead = text[i];
        if (lead < 0x80) {
            ++i;
            continue;
        }
        // Continuation bytes are 0x80-0xBF; the first one's range is narrowed where the lead
        // alone would admit an overlong form, a surrogate or a code point above U+10FFFF.
        std::size_t tail = 0;
        unsigned char first_low = 0x80;
        unsigned char first_high = 0xBF;
        if (lead >= 0xC2 && lead <= 0xDF) {
            tail = 1;
        } else if (lead == 0xE0) {
            tail = 2;
            first_low = 0xA0;
        } else if ((lead >= 0xE1 && lead <= 0xEC) || lead == 0xEE || lead == 0xEF) {
            tail = 2;
        } else if (lead == 0xED) {
            tail = 2;
            first_high = 0x9F;
        } else if (lead == 0xF0) {
            tail = 3;
            first_low = 0x90;
        } else if (lead >= 0xF1 && lead <= 0xF3) {
            tail = 3;
        } else if (lead == 0xF4) {
            tail = 3;
            first_high = 0x8F;
        } else {
            // 0x80-0xBF: a stray continuation byte; 0xC0-0xC1: an overlong two-byte form;
            // 0xF5-0xFF: never valid.
            return false;
        }
        if (length - i - 1 < tail) {
            return false;
        }
        if (text[i + 1] < first_low || text[i + 1] > first_high) {
            return false;
        }
        for (std::size_t k = 2; k <= tail; ++k) {
            if ((text[i + k] & 0xC0u) != 0x80u) {
                return false;
            }
        }
        i += tail + 1;
    }
    return true;
}

/**
 * Length of a step name, or RQ_NAME_SIZE when it has no NUL within its RQ_NAME_SIZE bytes.
 * Reads only the inline array.
 */
std::size_t name_length(const rq_step_def& step) noexcept {
    const void* const nul = std::memchr(step.name, '\0', RQ_NAME_SIZE);
    return nul == nullptr ? RQ_NAME_SIZE
                          : static_cast<std::size_t>(static_cast<const char*>(nul) - step.name);
}

bool is_moving_inversion(std::uint32_t pattern) noexcept {
    return pattern == RQ_PATTERN_MOVING_INVERSION_WALKING ||
           pattern == RQ_PATTERN_MOVING_INVERSION_RANDOM;
}

/**
 * The per-step rules of validate_config for steps[index], whose predecessors have already
 * passed: name, uniqueness against the earlier steps, value ranges, budgets and shape.
 */
bool step_is_valid(const rq_step_def* steps, std::uint32_t index) noexcept {
    const rq_step_def& step = steps[index];

    const std::size_t length = name_length(step);
    if (length == 0 || length >= RQ_NAME_SIZE ||
        !is_valid_utf8(reinterpret_cast<const unsigned char*>(step.name), length)) {
        return false;
    }
    // Allocation-free pairwise comparison: at most k_max_step_count steps of 63 bytes.
    for (std::uint32_t earlier = 0; earlier < index; ++earlier) {
        if (name_length(steps[earlier]) == length &&
            std::memcmp(steps[earlier].name, step.name, length) == 0) {
            return false;
        }
    }

    if (step.pattern < RQ_PATTERN_CHECKERBOARD || step.pattern > RQ_PATTERN_HIGH_TOGGLE) {
        return false;
    }
    if (step.access_order < RQ_ORDER_SEQUENTIAL_ASCENDING ||
        step.access_order > RQ_ORDER_TURNAROUND) {
        return false;
    }
    if (step.thread_mode != RQ_THREADS_ALL && step.thread_mode != RQ_THREADS_HALF) {
        return false;
    }
    if (step.time_budget_ms == 0 || step.coverage_budget_bp == 0) {
        return false;
    }

    // Only the shapes the step runner defines: moving inversion is whole-slice sweeps without a
    // hold, a hold step writes its slice in random-block order, and turnaround has no hold.
    if (is_moving_inversion(step.pattern) && step.hold_ms != 0) {
        return false;
    }
    if (step.hold_ms > 0 &&
        (is_moving_inversion(step.pattern) || step.access_order != RQ_ORDER_RANDOM_BLOCKS)) {
        return false;
    }
    if (step.access_order == RQ_ORDER_TURNAROUND && step.hold_ms != 0) {
        return false;
    }
    return true;
}

/** Text for an end reason recorded without a detail; never used for RQ_END_COMPLETED. */
const char* default_end_detail(std::uint32_t end_reason) noexcept {
    switch (end_reason) {
    case RQ_END_FIRST_ERROR:
        return "First confirmed error with Stop on error set";
    case RQ_END_USER_STOP:
        return "Stop requested by host: user";
    case RQ_END_THERMAL:
        return "Stop requested by host: thermal limit";
    case RQ_END_HOST_FAULT:
        return "Stop requested by host: host fault";
    case RQ_END_INSUFFICIENT_MEMORY:
        return "Not enough lockable memory for the minimum test region";
    case RQ_END_AFFINITY_FAILED:
        return "SetThreadGroupAffinity failed";
    default:
        return "Internal engine error";
    }
}

std::shared_ptr<IClock> clock_or_default(std::shared_ptr<IClock> clock) {
    if (clock) {
        return clock;
    }
    return std::make_shared<qpc_clock>();
}

std::shared_ptr<IThreadAffinity> affinity_or_default(std::shared_ptr<IThreadAffinity> affinity) {
    if (affinity) {
        return affinity;
    }
    return make_win32_thread_affinity();
}

/**
 * What one run creates on the coordinator, held outside the guarded body so the tail can still
 * stop and join the workers, and release everything, after an exception left that body. Members
 * are declared in dependency order, so destruction runs runner first and collector last.
 */
struct run_resources {
    std::unique_ptr<error_collector> errors;
    std::unique_ptr<coverage_counter> coverage;
    std::unique_ptr<worker_pool> pool;
    std::vector<slice_view> slices;
    std::unique_ptr<step_runner> runner;

    /** True once on_prepared has been raised; test time exists only then. */
    bool prepared = false;
    /** Test time zero: the clock reading at which preparation completed. */
    std::uint64_t t0_ns = 0;
    /** Bytes under test, after slice rounding and any lock shrink. */
    std::uint64_t tested_bytes = 0;

    // Error-batch bookkeeping: when the collector was last drained, and what the host has
    // received, so the coordinator can tell whether anything is still undelivered.
    bool drained_once = false;
    std::uint64_t last_drain_ns = 0;
    std::uint64_t delivered_total = 0;
    std::uint64_t delivered_records = 0;

    // The step whose gate continued and whose on_step_finished is still owed, so the tail can
    // close it if an exception leaves run_step. open_step_passes follows the observer's ticks.
    bool step_open = false;
    std::uint32_t open_step = 0;
    std::uint64_t open_step_start_ns = 0;
    std::uint32_t open_step_passes = 0;
};

/**
 * The handle registry. Handles are compared by value only and dereferenced only after they are
 * found here, so null, never-issued and destroyed handles are rejected without being touched.
 */
struct session_registry {
    std::mutex mutex;
    std::unordered_set<const rq_session_t*> handles;
};

session_registry& registry() {
    static session_registry instance;
    return instance;
}

// Mirrors registry().handles.size(), updated under the registry lock, so live_session_count()
// needs neither the lock nor the registry's lazy construction and has no failure path.
std::atomic<std::uint32_t> live_sessions{0};

}  // namespace

/* ---------------------------------------------------------------------------------------------
 * Configuration validation
 * ------------------------------------------------------------------------------------------- */

rq_result validate_config(const rq_session_config* config, const rq_callbacks* callbacks,
                          const validation_limits& limits) noexcept {
    // Structure, checked before any field it guards is read.
    if (config == nullptr || callbacks == nullptr) {
        return RQ_E_INVALID_ARGUMENT;
    }
    if (config->struct_size != sizeof(rq_session_config) ||
        callbacks->struct_size != sizeof(rq_callbacks) ||
        config->step_def_size != sizeof(rq_step_def)) {
        return RQ_E_INVALID_ARGUMENT;
    }
    if (config->step_count == 0 || config->step_count > k_max_step_count ||
        config->steps == nullptr) {
        return RQ_E_INVALID_ARGUMENT;
    }

    for (std::uint32_t i = 0; i < config->step_count; ++i) {
        if (!step_is_valid(config->steps, i)) {
            return RQ_E_INVALID_ARGUMENT;
        }
    }

    // Region and workers.
    if (config->min_region_bytes == 0 || config->region_bytes < config->min_region_bytes) {
        return RQ_E_INVALID_ARGUMENT;
    }
    if (config->worker_count == 0 || config->worker_count > limits.logical_processors) {
        return RQ_E_INVALID_ARGUMENT;
    }
    if (config->min_region_bytes / config->worker_count < k_min_slice_bytes) {
        return RQ_E_INVALID_ARGUMENT;
    }

    // Run options.
    if (config->stop_on_error > 1 || config->error_detail_cap > k_max_error_detail_cap ||
        config->progress_interval_ms == 0) {
        return RQ_E_INVALID_ARGUMENT;
    }
    // Forcing a set the CPU lacks would execute instructions it cannot decode, an
    // illegal-instruction SEH fault the engine never catches.
    if (config->isa_override != RQ_ISA_AUTO &&
        (config->isa_override > RQ_ISA_AVX512 ||
         ((limits.isa_available_mask >> config->isa_override) & 1u) == 0)) {
        return RQ_E_INVALID_ARGUMENT;
    }

    // Callbacks: all seven are required.
    if (callbacks->on_prepared == nullptr || callbacks->on_step_starting == nullptr ||
        callbacks->on_step_finished == nullptr || callbacks->on_progress == nullptr ||
        callbacks->on_errors == nullptr || callbacks->on_completed == nullptr ||
        callbacks->on_released == nullptr) {
        return RQ_E_INVALID_ARGUMENT;
    }
    return RQ_OK;
}

rq_result validate_config(const rq_session_config* config, const rq_callbacks* callbacks) {
    const std::vector<logical_processor> processors = enumerate_logical_processors();
    validation_limits limits{};
    limits.logical_processors =
        processors.size() > UINT32_MAX ? UINT32_MAX : static_cast<std::uint32_t>(processors.size());
    limits.isa_available_mask = select_isa(read_cpu_features(), RQ_ISA_AUTO).available_mask;
    return validate_config(config, callbacks, limits);
}

/* ---------------------------------------------------------------------------------------------
 * Time remaining
 * ------------------------------------------------------------------------------------------- */

std::int64_t compute_remaining_ms(const remaining_inputs& in) noexcept {
    if (in.step_index == 0 && in.passes_done == 0) {
        return -1;
    }
    if (in.step_index >= in.steps.size()) {
        return 0;
    }
    const rq_step_def& running = in.steps[in.step_index];
    const std::uint64_t target_ns = in.passes_done == 0
                                        ? saturating_mul(running.time_budget_ms, k_ns_per_ms)
                                        : saturating_mul(in.passes_planned, in.mean_pass_ns);
    const std::uint64_t term_ns =
        target_ns > in.step_elapsed_ns ? target_ns - in.step_elapsed_ns : 0;

    std::uint64_t remaining_ms = term_ns / k_ns_per_ms;
    for (std::size_t later = std::size_t{in.step_index} + 1; later < in.steps.size(); ++later) {
        remaining_ms = saturating_add(remaining_ms, in.steps[later].time_budget_ms);
    }
    return remaining_ms > static_cast<std::uint64_t>(INT64_MAX)
               ? INT64_MAX
               : static_cast<std::int64_t>(remaining_ms);
}

/* ---------------------------------------------------------------------------------------------
 * Session state and coordinator
 * ------------------------------------------------------------------------------------------- */

/**
 * Everything one session owns. Members are declared in dependency order: the coordinator
 * thread is last, so it is destroyed first (always already joined), and the clock outlives the
 * memory-access layers, which may read it.
 */
struct session::state {
    state(const rq_session_config& config, const rq_callbacks& callbacks,
          session_dependencies dependencies);

    state(const state&) = delete;
    state& operator=(const state&) = delete;
    state(state&&) = delete;
    state& operator=(state&&) = delete;

    rq_result start();
    rq_result stop(std::uint32_t stop_reason) noexcept;
    rq_result get_result(rq_run_result* out) const noexcept;
    bool on_coordinator_thread() const noexcept;
    void shutdown();

    /** The observer run_step ticks: raises progress and error batches on their cadences. */
    class step_ticker;

    void coordinator_main() noexcept;
    void run_session(run_resources& run);
    bool prepare(run_resources& run);
    void run_plan(run_resources& run);
    void finish(run_resources& run) noexcept;

    void raise_progress(const run_resources& run, const step_status& status,
                        std::uint64_t now_ns) const noexcept;
    void deliver_errors(run_resources& run, std::uint64_t now_ns) const noexcept;
    bool has_undelivered_errors(const run_resources& run) const noexcept;
    void deliver_closing_batch(run_resources& run);
    void close_step(run_resources& run, bool completed, std::uint64_t actual_ns,
                    std::uint64_t verified_bytes) noexcept;
    std::uint32_t raise_step_gate(const rq_step_start& info) const noexcept;

    /**
     * Raises one callback unless it is null or the host has destroyed the session. The single
     * path for every callback except the step gate, which returns a value.
     */
    template <typename Callback, typename... Args>
    void raise(Callback callback, Args... args) const noexcept {
        if (callback == nullptr || callbacks_suppressed_.load(std::memory_order_acquire)) {
            return;
        }
        callback(callbacks_.user_data, args...);
    }

    // Deep copies, immutable after construction. config_.steps points into steps_.
    rq_session_config config_;
    std::vector<rq_step_def> steps_;
    rq_callbacks callbacks_;

    // Seams, kept for the session's lifetime.
    std::shared_ptr<IClock> clock_;
    std::shared_ptr<IThreadAffinity> affinity_;

    // ISA dispatch, run once at creation and fixed for the session.
    isa_selection isa_;
    kernel_table kernels_;

    stop_signal stop_;
    // The outermost memory-access layer; the runner and the error collector issue every
    // primitive and re-read through it.
    std::unique_ptr<IMemoryAccess> memory_;
    // Allocated and released by the coordinator; released by shutdown() when it never ran.
    memory_region region_;

    // Reserved for step_count entries at construction, so appending never reallocates and the
    // pointers handed out (on_step_finished, rq_run_result.step_results) stay valid.
    std::vector<rq_step_result> step_results_;
    // Written once, by the coordinator, before result_ready_ is published.
    rq_run_result result_{};
    std::atomic<bool> result_ready_{false};
    std::atomic<bool> callbacks_suppressed_{false};
    // The coordinator's thread id while it runs, 0 otherwise; never 0 for a live thread.
    std::atomic<DWORD> coordinator_tid_{0};

    // Guards started_, shut_down_ and coordinator_, so the std::thread is never read while it
    // is being assigned.
    std::mutex lifecycle_mutex_;
    bool started_ = false;
    bool shut_down_ = false;
    std::thread coordinator_;
};

class session::state::step_ticker final : public step_observer {
public:
    step_ticker(state& owner, run_resources& run) noexcept
        : owner_(owner), run_(run),
          progress_interval_ns_(std::uint64_t{owner.config_.progress_interval_ms} * k_ns_per_ms) {}

    /**
     * Raises on_progress every progress_interval_ms of session-clock time, the first at the
     * run's first tick, and drains the error collector at most every k_error_batch_interval_ns,
     * raising on_errors when the drain has news.
     */
    void tick(const step_status& status) override {
        run_.open_step_passes = status.passes_done;
        const std::uint64_t now = owner_.clock_->now_ns();
        if (!progress_raised_ || elapsed_since(now, last_progress_ns_) >= progress_interval_ns_) {
            owner_.raise_progress(run_, status, now);
            progress_raised_ = true;
            last_progress_ns_ = now;
        }
        if (!run_.drained_once ||
            elapsed_since(now, run_.last_drain_ns) >= k_error_batch_interval_ns) {
            owner_.deliver_errors(run_, now);
        }
    }

private:
    state& owner_;
    run_resources& run_;
    const std::uint64_t progress_interval_ns_;
    bool progress_raised_ = false;
    std::uint64_t last_progress_ns_ = 0;
};

session::state::state(const rq_session_config& config, const rq_callbacks& callbacks,
                      session_dependencies dependencies)
    : config_(config), steps_(config.steps, config.steps + config.step_count),
      callbacks_(callbacks), clock_(clock_or_default(std::move(dependencies.clock))),
      affinity_(affinity_or_default(std::move(dependencies.affinity))),
      isa_(select_isa(read_cpu_features(), config.isa_override)), kernels_(tables_for(isa_)) {
    // validate_config has already checked the override against this machine; a rejection here
    // means the configuration was never validated.
    if (isa_.override_rejected) {
        throw std::invalid_argument("isa_override names an instruction set this CPU lacks");
    }
    // The copy never refers to the caller's array, which may be freed once creation returns.
    config_.steps = steps_.data();

    std::unique_ptr<IMemoryAccess> real = std::make_unique<real_memory_access>(kernels_);
    memory_ = dependencies.wrap_memory_access ? dependencies.wrap_memory_access(std::move(real))
                                              : std::move(real);
    if (!memory_) {
        throw std::invalid_argument("wrap_memory_access returned no memory-access layer");
    }
    step_results_.reserve(config.step_count);
}

rq_result session::state::start() {
    std::lock_guard<std::mutex> lock(lifecycle_mutex_);
    if (started_ || shut_down_) {
        return RQ_E_INVALID_STATE;
    }
    // Set before the attempt: start is allowed once whatever its outcome, so a failed thread
    // creation leaves a session that can only be destroyed and never raises a callback.
    started_ = true;
    try {
        coordinator_ = std::thread([this]() noexcept { coordinator_main(); });
    } catch (const std::system_error&) {
        return RQ_E_THREAD_CREATE;
    }
    return RQ_OK;
}

rq_result session::state::stop(std::uint32_t stop_reason) noexcept {
    std::uint32_t end_reason = 0;
    const char* detail = nullptr;
    switch (stop_reason) {
    case RQ_STOP_USER:
        end_reason = RQ_END_USER_STOP;
        detail = "Stop requested by host: user";
        break;
    case RQ_STOP_THERMAL:
        end_reason = RQ_END_THERMAL;
        detail = "Stop requested by host: thermal limit";
        break;
    case RQ_STOP_HOST_FAULT:
        end_reason = RQ_END_HOST_FAULT;
        detail = "Stop requested by host: host fault";
        break;
    default:
        return RQ_E_INVALID_ARGUMENT;
    }
    // Recorded before returning; first writer wins, so a later call, or a call after
    // completion, changes nothing.
    stop_.request(end_reason, 0, detail);
    return RQ_OK;
}

rq_result session::state::get_result(rq_run_result* out) const noexcept {
    if (out == nullptr || out->struct_size != sizeof(rq_run_result)) {
        return RQ_E_INVALID_ARGUMENT;
    }
    if (!result_ready_.load(std::memory_order_acquire)) {
        return RQ_E_INVALID_STATE;
    }
    *out = result_;
    return RQ_OK;
}

bool session::state::on_coordinator_thread() const noexcept {
    const DWORD coordinator = coordinator_tid_.load(std::memory_order_acquire);
    return coordinator != 0 && coordinator == GetCurrentThreadId();
}

void session::state::shutdown() {
    std::thread coordinator;
    {
        std::lock_guard<std::mutex> lock(lifecycle_mutex_);
        shut_down_ = true;
        // First writer wins, so an end reason recorded earlier is kept.
        stop_.request(RQ_END_USER_STOP, 0, "Session destroyed by host");
        // The callback in flight, if any, returns normally; nothing new is raised after it.
        callbacks_suppressed_.store(true, std::memory_order_release);
        coordinator = std::move(coordinator_);
    }
    // Joining cannot deadlock against the managed UI thread: the wrapper posts every event to
    // its SynchronizationContext instead of blocking the coordinator on it, and its step gate
    // waits only on its journal acknowledgment, bounded at 5 s, and its own stop event.
    if (coordinator.joinable()) {
        coordinator.join();
    }
    // The coordinator releases the region in its tail; this covers a coordinator that never
    // ran. Idempotent.
    region_.release();
}

void session::state::coordinator_main() noexcept {
    coordinator_tid_.store(GetCurrentThreadId(), std::memory_order_release);
    run_resources run;
    try {
        run_session(run);
    } catch (const std::exception& e) {
        stop_.request(RQ_END_INTERNAL_FAULT, 0, e.what());
    } catch (...) {
        stop_.request(RQ_END_INTERNAL_FAULT, 0, "Unknown C++ exception");
    }
    // Outside the guarded body, so it runs for every end reason, after an exception as well:
    // the request above has already told any running phase task to return.
    finish(run);
    coordinator_tid_.store(0, std::memory_order_release);
}

void session::state::run_session(run_resources& run) {
    // A stop recorded before start: Completed and Released only, with no Prepared.
    if (stop_.stop_requested()) {
        return;
    }
    const std::uint64_t prepare_start_ns = clock_->now_ns();
    if (!prepare(run)) {
        return;
    }

    // Test time zero is taken here, immediately before on_prepared, as ramquick_engine.h
    // defines it, rather than after the callback returns; so prepare_ms and test time partition
    // the run, and error times agree with the elapsed time the host shows.
    run.t0_ns = clock_->now_ns();
    run.prepared = true;
    run.errors->begin_run(run.t0_ns);

    rq_prepared info{};
    info.struct_size = sizeof(rq_prepared);
    info.worker_count = run.pool->worker_count();
    info.page_mode = region_.page_mode();
    info.isa_selected = isa_.selected;
    info.address_kind = RQ_ADDRESS_KIND_VIRTUAL;
    info.reserved0 = 0;
    info.tested_bytes = run.tested_bytes;
    info.prepare_ms = elapsed_since(run.t0_ns, prepare_start_ns) / k_ns_per_ms;
    raise(callbacks_.on_prepared, static_cast<const rq_prepared*>(&info));

    run_plan(run);
}

bool session::state::prepare(run_resources& run) {
    std::vector<logical_processor> placements = enumerate_logical_processors();
    if (placements.size() < config_.worker_count) {
        char detail[RQ_END_DETAIL_SIZE];
        std::snprintf(detail, sizeof(detail),
                      "Only %zu logical processors are active; the session needs %u workers",
                      placements.size(), config_.worker_count);
        stop_.request(RQ_END_INTERNAL_FAULT, 0, detail);
        return false;
    }
    // Group-major order: worker i is pinned to the i-th active logical processor.
    placements.resize(config_.worker_count);

    // Error-detail storage is preallocated here, at start, never at create.
    error_collector_options options;
    options.detail_cap = config_.error_detail_cap;
    options.stop_on_error = config_.stop_on_error != 0;
    options.step_count = config_.step_count;
    options.placements = placements;
    run.errors = std::make_unique<error_collector>(options, stop_, *clock_, *memory_);
    run.coverage = std::make_unique<coverage_counter>(config_.worker_count);

    region_options request{};
    request.region_bytes = config_.region_bytes;
    request.min_region_bytes = config_.min_region_bytes;
    request.worker_count = config_.worker_count;
    request.lock_privilege_held = lock_memory_privilege_held();
    const allocation_outcome allocated = region_.allocate(request, stop_);
    switch (allocated.status) {
    case allocation_status::ok:
        break;
    case allocation_status::stopped:
        // The host's stop reason is already recorded.
        return false;
    case allocation_status::insufficient_memory:
        stop_.request(RQ_END_INSUFFICIENT_MEMORY, allocated.os_error, allocated.end_detail);
        return false;
    }
    run.tested_bytes = region_.tested_bytes();

    run.pool = std::make_unique<worker_pool>();
    const pool_start_result started =
        run.pool->start(placements, config_.worker_count, *affinity_, stop_);
    switch (started.outcome) {
    case pool_start_result::status::ok:
        break;
    case pool_start_result::status::affinity_failed: {
        // No traffic has run: an unpinned run must never produce a verdict.
        char detail[RQ_END_DETAIL_SIZE];
        std::snprintf(
            detail, sizeof(detail), "SetThreadGroupAffinity failed: group %u, processor %u",
            static_cast<unsigned>(started.group), static_cast<unsigned>(started.processor));
        stop_.request(RQ_END_AFFINITY_FAILED, started.os_error, detail);
        return false;
    }
    case pool_start_result::status::thread_create_failed: {
        char detail[RQ_END_DETAIL_SIZE];
        std::snprintf(detail, sizeof(detail), "CreateThread failed for worker %u",
                      started.failed_worker);
        stop_.request(RQ_END_INTERNAL_FAULT, started.os_error, detail);
        return false;
    }
    case pool_start_result::status::stopped:
        return false;
    }
    if (started.priority_failures != 0) {
        // Priority only protects responsiveness, so the run continues.
        debug_log(L"SetThreadPriority(THREAD_PRIORITY_BELOW_NORMAL) failed for %u of %u workers "
                  L"(first Windows error %u); the run continues",
                  started.priority_failures, config_.worker_count, started.first_priority_error);
    }

    run.slices.reserve(config_.worker_count);
    for (std::uint32_t worker = 0; worker < config_.worker_count; ++worker) {
        run.slices.push_back(region_.slice(worker));
    }
    run.runner = std::make_unique<step_runner>(step_runner_dependencies{
        .slices = run.slices,
        .memory = *memory_,
        .pool = *run.pool,
        .errors = *run.errors,
        .coverage = *run.coverage,
        .clock = *clock_,
        .stop = stop_,
        .seed = config_.seed,
    });
    return true;
}

void session::state::run_plan(run_resources& run) {
    step_ticker ticker(*this, run);
    bool every_step_ran = true;
    for (std::uint32_t index = 0; index < config_.step_count; ++index) {
        if (stop_.stop_requested()) {
            every_step_ran = false;
            break;
        }
        const rq_step_def& step = steps_[index];

        rq_step_start start_info{};
        start_info.struct_size = sizeof(rq_step_start);
        start_info.index = index;
        start_info.count = config_.step_count;
        start_info.reserved0 = 0;
        copy_utf8_truncated(start_info.name, sizeof(start_info.name), step.name);
        if (raise_step_gate(start_info) != RQ_ACTION_CONTINUE) {
            // An aborted gate starts no traffic and, as ramquick_engine.h specifies, gets no
            // on_step_finished and no step_results entry: step_results holds one entry per
            // step whose gate continued. The run ends with the pending stop reason, or
            // host_fault when the host recorded none.
            if (stop_.pending_reason() == 0) {
                char detail[256];
                std::snprintf(detail, sizeof(detail), "Step gate aborted step %u of %u (%s)",
                              index + 1, config_.step_count, step.name);
                stop_.request(RQ_END_HOST_FAULT, 0, detail);
            }
            every_step_ran = false;
            break;
        }

        run.step_open = true;
        run.open_step = index;
        run.open_step_start_ns = clock_->now_ns();
        run.open_step_passes = 0;
        const step_outcome outcome = run.runner->run_step(step, index, ticker);
        run.open_step_passes = outcome.passes;
        // Every record the step produced reaches the host before its on_step_finished.
        deliver_closing_batch(run);
        close_step(run, outcome.completed, outcome.actual_ns, outcome.verified_bytes);

        if (!outcome.completed) {
            every_step_ran = false;
            break;
        }
    }

    // Freezes the end reason. First writer wins, which closes the race between the last step
    // finishing and a late stop or first error: whichever was recorded first decides.
    if (every_step_ran) {
        stop_.request(RQ_END_COMPLETED);
    } else if (stop_.pending_reason() == 0) {
        // run_step reports an incomplete step only when it observed a stop, so no reason here
        // means a broken invariant; it must not read as a completed run.
        stop_.request(RQ_END_INTERNAL_FAULT, 0,
                      "A step ended before its schedule without a recorded stop");
    }
}

void session::state::raise_progress(const run_resources& run, const step_status& status,
                                    std::uint64_t now_ns) const noexcept {
    remaining_inputs remaining{};
    remaining.steps = steps_;
    remaining.step_index = status.step_index;
    remaining.passes_done = status.passes_done;
    remaining.passes_planned = status.passes_planned;
    remaining.mean_pass_ns = status.mean_pass_ns;
    remaining.step_elapsed_ns = status.step_elapsed_ns;

    rq_progress progress{};
    progress.struct_size = sizeof(rq_progress);
    progress.step_index = status.step_index;
    progress.step_count = config_.step_count;
    progress.pass_index = status.pass_index;
    progress.passes_planned = status.passes_planned;
    progress.reserved0 = 0;
    progress.elapsed_ms = elapsed_since(now_ns, run.t0_ns) / k_ns_per_ms;
    progress.step_elapsed_ms = status.step_elapsed_ns / k_ns_per_ms;
    progress.remaining_ms = compute_remaining_ms(remaining);
    progress.verified_bytes = run.coverage->session_total();
    progress.region_bytes = run.tested_bytes;
    progress.total_errors = run.errors->true_total();
    raise(callbacks_.on_progress, static_cast<const rq_progress*>(&progress));
}

void session::state::deliver_errors(run_resources& run, std::uint64_t now_ns) const noexcept {
    // The batch points into the collector's own storage, valid for the call; after the detail
    // cap it carries count 0 with the rising true total.
    const error_batch batch = run.errors->drain();
    run.drained_once = true;
    run.last_drain_ns = now_ns;
    run.delivered_total = batch.total_errors;
    run.delivered_records += batch.count;
    if (batch.has_news) {
        raise(callbacks_.on_errors, batch.records, batch.count, batch.total_errors);
    }
}

bool session::state::has_undelivered_errors(const run_resources& run) const noexcept {
    return run.errors != nullptr && (run.errors->true_total() != run.delivered_total ||
                                     run.errors->stored_count() > run.delivered_records);
}

void session::state::deliver_closing_batch(run_resources& run) {
    if (!has_undelivered_errors(run)) {
        return;
    }
    if (run.drained_once) {
        const std::uint64_t since = elapsed_since(clock_->now_ns(), run.last_drain_ns);
        if (since < k_error_batch_interval_ns) {
            // Keeps batches 100 ms apart. The wait returns at once when a stop is pending, so
            // stop latency wins over spacing, which costs at most one early batch per run: the
            // loop ends after a stop.
            const std::uint64_t wait_ns = k_error_batch_interval_ns - since;
            clock_->wait_for_stop(stop_, (wait_ns + k_ns_per_ms - 1) / k_ns_per_ms);
        }
    }
    deliver_errors(run, clock_->now_ns());
}

void session::state::close_step(run_resources& run, bool completed, std::uint64_t actual_ns,
                                std::uint64_t verified_bytes) noexcept {
    const rq_step_def& step = steps_[run.open_step];
    rq_step_result result{};
    result.index = run.open_step;
    result.completed = completed ? 1u : 0u;
    result.passes = run.open_step_passes;
    result.reserved0 = 0;
    result.actual_ms = actual_ns / k_ns_per_ms;
    result.overrun_ms =
        result.actual_ms > step.time_budget_ms ? result.actual_ms - step.time_budget_ms : 0;
    result.verified_bytes = verified_bytes;
    result.errors = run.errors->step_errors(run.open_step);
    // Never allocates: the vector holds capacity for one entry per step, and a step is closed
    // at most once.
    step_results_.push_back(result);
    run.step_open = false;
    raise(callbacks_.on_step_finished, static_cast<const rq_step_result*>(&step_results_.back()));
}

std::uint32_t session::state::raise_step_gate(const rq_step_start& info) const noexcept {
    // A destroyed session runs no further step: suppression reads as an abort.
    if (callbacks_.on_step_starting == nullptr ||
        callbacks_suppressed_.load(std::memory_order_acquire)) {
        return RQ_ACTION_ABORT;
    }
    return callbacks_.on_step_starting(callbacks_.user_data, &info);
}

void session::state::finish(run_resources& run) noexcept {
    // Workers join before Completed. A stop or an exception has already been recorded when the
    // pool is still mid-phase, so running tasks return within one 1 MiB block.
    if (run.pool) {
        run.pool->shutdown();
    }
    // Every stored record and the final total precede Completed. A reason is always recorded
    // by now, so no spacing wait applies.
    if (has_undelivered_errors(run)) {
        deliver_errors(run, clock_->now_ns());
    }
    // A step an exception interrupted still gets its result, cut short, so every step whose
    // gate continued has exactly one on_step_finished before on_completed. With the workers
    // joined, the step's coverage and error counts are exact.
    if (run.step_open) {
        close_step(run, false, elapsed_since(clock_->now_ns(), run.open_step_start_ns),
                   run.coverage->step_total());
    }
    const std::uint64_t end_ns = clock_->now_ns();

    if (stop_.pending_reason() == 0) {
        stop_.request(RQ_END_INTERNAL_FAULT, 0, "Coordinator ended without an end reason");
    }
    const stop_signal::end_info end = stop_.snapshot();

    std::uint32_t steps_completed = 0;
    for (const rq_step_result& step : step_results_) {
        steps_completed += step.completed;
    }

    result_ = rq_run_result{};
    result_.struct_size = sizeof(rq_run_result);
    result_.end_reason = end.reason;
    result_.steps_completed = steps_completed;
    result_.step_result_count = static_cast<std::uint32_t>(step_results_.size());
    result_.total_errors = run.errors ? run.errors->true_total() : 0;
    result_.verified_bytes = run.coverage ? run.coverage->session_total() : 0;
    result_.elapsed_ms = run.prepared ? elapsed_since(end_ns, run.t0_ns) / k_ns_per_ms : 0;
    result_.first_error_ms = run.errors ? run.errors->first_error_ms() : RQ_NO_FIRST_ERROR;
    result_.step_results = step_results_.data();
    result_.reserved0 = 0;
    if (end.reason == RQ_END_COMPLETED) {
        // Diagnostics are reserved for the other end reasons.
        result_.os_error = 0;
    } else {
        result_.os_error = end.os_error;
        const char* const detail =
            end.detail[0] != '\0' ? end.detail : default_end_detail(end.reason);
        const std::uint32_t signal_error = stop_.signal_error();
        if (signal_error != 0) {
            // A stop event that could not be signalled only added latency (waits recheck the
            // flag), so it is reported beside the reason rather than replacing it.
            char annotated[RQ_END_DETAIL_SIZE + 64];
            std::snprintf(annotated, sizeof(annotated),
                          "%s (stop event not signalled: Windows error %u)", detail, signal_error);
            copy_utf8_truncated(result_.end_detail, sizeof(result_.end_detail), annotated);
        } else {
            copy_utf8_truncated(result_.end_detail, sizeof(result_.end_detail), detail);
        }
    }
    if (stop_.signal_error() != 0) {
        debug_log(L"SetEvent on the stop event failed (Windows error %u)", stop_.signal_error());
    }
    result_ready_.store(true, std::memory_order_release);
    raise(callbacks_.on_completed, static_cast<const rq_run_result*>(&result_));

    // The run's objects go before the region: the workers have joined, and nothing refers to
    // the collector's records once on_completed has returned.
    run.runner.reset();
    run.slices.clear();
    run.pool.reset();
    run.errors.reset();
    run.coverage.reset();

    // Released also when callbacks are suppressed, so no exit path leaves memory locked.
    const std::uint64_t release_start_ns = clock_->now_ns();
    const std::uint64_t released_bytes = region_.release();
    const std::uint64_t release_ms =
        released_bytes == 0 ? 0 : elapsed_since(clock_->now_ns(), release_start_ns) / k_ns_per_ms;
    raise(callbacks_.on_released, released_bytes, release_ms);
}

/* ---------------------------------------------------------------------------------------------
 * session
 * ------------------------------------------------------------------------------------------- */

session::session(const rq_session_config& config, const rq_callbacks& callbacks,
                 session_dependencies dependencies)
    : state_(std::make_unique<state>(config, callbacks, std::move(dependencies))) {}

session::~session() {
    shutdown();
}

rq_result session::start() {
    return state_->start();
}

rq_result session::stop(std::uint32_t stop_reason) noexcept {
    return state_->stop(stop_reason);
}

rq_result session::get_result(rq_run_result* out) const noexcept {
    return state_->get_result(out);
}

bool session::on_coordinator_thread() const noexcept {
    return state_->on_coordinator_thread();
}

void session::shutdown() {
    state_->shutdown();
}

IMemoryAccess& session::memory_access() noexcept {
    return *state_->memory_;
}

std::uint32_t session::pending_stop_reason() const noexcept {
    return state_->stop_.pending_reason();
}

/* ---------------------------------------------------------------------------------------------
 * Handle-level functions
 * ------------------------------------------------------------------------------------------- */

rq_result session_create(const rq_session_config* config, const rq_callbacks* callbacks,
                         session_dependencies dependencies, rq_session_handle* out) {
    if (out == nullptr) {
        return RQ_E_INVALID_ARGUMENT;
    }
    *out = nullptr;
    const rq_result validation = validate_config(config, callbacks);
    if (validation != RQ_OK) {
        return validation;
    }

    std::shared_ptr<session> created;
    try {
        created = std::make_shared<session>(*config, *callbacks, std::move(dependencies));
    } catch (const std::invalid_argument&) {
        return RQ_E_INVALID_ARGUMENT;
    }

    // The engine's only raw new: the C API hands out a plain pointer whose lifetime ends at
    // session_destroy. The unique_ptr frees it if registration throws.
    std::unique_ptr<rq_session_t> handle(new rq_session_t{std::move(created)});
    {
        session_registry& sessions = registry();
        std::lock_guard<std::mutex> lock(sessions.mutex);
        sessions.handles.insert(handle.get());
        live_sessions.fetch_add(1, std::memory_order_relaxed);
    }
    *out = handle.release();
    return RQ_OK;
}

rq_result session_start(rq_session_handle handle) {
    const std::shared_ptr<session> target = acquire_session(handle);
    return target ? target->start() : RQ_E_INVALID_HANDLE;
}

rq_result session_stop(rq_session_handle handle, std::uint32_t stop_reason) {
    const std::shared_ptr<session> target = acquire_session(handle);
    return target ? target->stop(stop_reason) : RQ_E_INVALID_HANDLE;
}

rq_result session_get_result(rq_session_handle handle, rq_run_result* out) {
    const std::shared_ptr<session> target = acquire_session(handle);
    return target ? target->get_result(out) : RQ_E_INVALID_HANDLE;
}

rq_result session_destroy(rq_session_handle handle) {
    // Adopts the handle once it is unregistered; its destruction is the matching raw delete.
    std::unique_ptr<rq_session_t> owned;
    {
        session_registry& sessions = registry();
        std::lock_guard<std::mutex> lock(sessions.mutex);
        const auto found = sessions.handles.find(handle);
        if (found == sessions.handles.end()) {
            return RQ_E_INVALID_HANDLE;
        }
        // From inside one of its own callbacks the coordinator would join itself; the session
        // stays registered and alive.
        if (handle->impl->on_coordinator_thread()) {
            return RQ_E_INVALID_STATE;
        }
        sessions.handles.erase(found);
        live_sessions.fetch_sub(1, std::memory_order_relaxed);
        owned.reset(handle);
    }
    // Outside the lock, so other sessions stay usable while this one stops and joins.
    owned->impl->shutdown();
    return RQ_OK;
}

std::shared_ptr<session> acquire_session(rq_session_handle handle) {
    session_registry& sessions = registry();
    std::lock_guard<std::mutex> lock(sessions.mutex);
    if (sessions.handles.find(handle) == sessions.handles.end()) {
        return nullptr;
    }
    return handle->impl;
}

std::uint32_t live_session_count() noexcept {
    return live_sessions.load(std::memory_order_relaxed);
}

}  // namespace rq::engine
