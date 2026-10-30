/**
 * @file test_api_boundary.cpp
 * @brief Contract tests of the flat C ABI in ramquick_engine.h, through the exports of api.cpp.
 *
 * - ApiBoundaryAbi: struct sizes and C layout, the noexcept boundary (no C++ exception can
 *   cross it: a throw that escaped an export would terminate the process, never propagate),
 *   the API version a host checks at bind time, and the result-code messages. noexcept alone
 *   cannot show that the exports' handlers exist; ApiBoundaryExceptions does.
 * - ApiBoundarySystem: rq_query_system's argument checks and its description of this machine.
 * - ApiBoundaryCreateInvalid and ApiBoundaryCreateValid: each validation rule of
 *   rq_session_create rejects its case with RQ_E_INVALID_ARGUMENT, leaves the handle null and
 *   registers nothing; configurations at the rules' limits are accepted.
 * - ApiBoundaryHandle: null, never-issued and destroyed handles, unknown stop reasons, double
 *   start and get_result before Completed.
 * - ApiBoundaryExceptions: a std::bad_alloc thrown inside rq_query_system and at every
 *   allocation of rq_session_create is translated by the export's handler into RQ_E_INTERNAL
 *   rather than terminating the process; the out struct stays unwritten, the handle is cleared
 *   and no session is registered. The exception comes from the allocation refusal below, which
 *   throws a C++ exception only, never an SEH fault.
 * - ApiBoundaryRun: the event order and payloads of a normal run, the deep copy, destroy from
 *   inside every callback and while running, and the forced instruction set.
 * - ApiBoundaryStopBeforeStart and ApiBoundaryGateAbort: first-writer-wins stop reasons and the
 *   step gate, including an abort with no recorded reason (RQ_END_HOST_FAULT).
 * - ApiBoundaryIsa: select_isa over synthetic CPUID and XCR0 input as the boundary applies it:
 *   without any of AVX-512 F, CD, BW, DQ or VL, or without the opmask and ZMM state, AVX-512 is
 *   not available and a configuration forcing it is invalid. test_kernels.cpp covers the full
 *   selection matrix; this suite covers what that selection means for isa_override.
 *
 * api.cpp is compiled into engine.tests.exe, so every rq_* call is the production export over
 * the production session registry. Started sessions test at most 64 MiB with at most two
 * workers and need no elevation. Every wait is bounded at 30 s, so a broken contract fails the
 * test instead of hanging the runner.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <new>
#include <set>
#include <string>
#include <thread>
#include <type_traits>
#include <vector>

#include "ramquick_engine.h"
#include "core/session.h"
#include "core/kernels/isa_dispatch.h"
#include "core/system_info.h"

/* ---------------------------------------------------------------------------------------------
 * Allocation refusal: the test-only failure seam of ApiBoundaryExceptions
 *
 * The exports build their production session_dependencies themselves, so no substitute layer
 * reachable through api.cpp can throw; allocation is the one throwing point every export shares.
 * These definitions replace the global allocation functions, as the standard permits a program
 * to, for the whole of engine.tests.exe: engine_core.lib, api.cpp and GoogleTest allocate
 * through them. Unarmed they behave as the defaults do, so every other test is unaffected.
 *
 * Armed on a thread, every request of at least k_refusable_bytes after the first pass_remaining
 * such requests throws std::bad_alloc and is counted. The state is per thread, so a thread the
 * engine or GoogleTest runs is never refused. Smaller requests always succeed: in Debug builds
 * (_ITERATOR_DEBUG_LEVEL 2) MSVC's noexcept std::vector default constructor allocates a 16-byte
 * _Container_proxy, where a refusal would call std::terminate instead of reaching an export's
 * handler. Array and nothrow forms forward to these functions in the MSVC runtime.
 * ------------------------------------------------------------------------------------------- */

namespace {

/** Smallest request the refusal fails. */
constexpr std::size_t k_refusable_bytes = 64;

/** Plain data, so the thread_local is constant-initialised and safe before main. */
struct allocation_refusal_state {
    bool armed;
    std::uint32_t pass_remaining;
    std::uint32_t refused;
};

constinit thread_local allocation_refusal_state refusal_state{false, 0, 0};

/** True when this request must throw; consumes one let-through request otherwise. */
bool refuse_allocation(std::size_t size) noexcept {
    allocation_refusal_state& state = refusal_state;
    if (!state.armed || size < k_refusable_bytes) {
        return false;
    }
    if (state.pass_remaining != 0) {
        --state.pass_remaining;
        return false;
    }
    ++state.refused;
    return true;
}

}  // namespace

void* operator new(std::size_t size) {
    if (refuse_allocation(size)) {
        throw std::bad_alloc();
    }
    // As the default does: a zero-size request still yields a unique pointer, and a failure
    // runs the installed new-handler until it frees memory or there is none left to run.
    const std::size_t bytes = size == 0 ? 1 : size;
    for (;;) {
        void* const block = std::malloc(bytes);
        if (block != nullptr) {
            return block;
        }
        const std::new_handler handler = std::get_new_handler();
        if (handler == nullptr) {
            throw std::bad_alloc();
        }
        handler();
    }
}

void operator delete(void* block) noexcept {
    std::free(block);
}

void operator delete(void* block, std::size_t) noexcept {
    std::free(block);
}

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Compile-time ABI contract
 * ------------------------------------------------------------------------------------------- */

/** Blittable for the managed mirror in NativeStructs.cs: standard layout, trivially copyable. */
template <typename T>
constexpr bool k_is_c_layout = std::is_standard_layout_v<T> && std::is_trivially_copyable_v<T>;

static_assert(sizeof(rq_system_info) == 112 && k_is_c_layout<rq_system_info>);
static_assert(sizeof(rq_step_def) == 96 && k_is_c_layout<rq_step_def>);
static_assert(sizeof(rq_session_config) == 64 && k_is_c_layout<rq_session_config>);
static_assert(sizeof(rq_callbacks) == 72 && k_is_c_layout<rq_callbacks>);
static_assert(sizeof(rq_prepared) == 40 && k_is_c_layout<rq_prepared>);
static_assert(sizeof(rq_step_start) == 80 && k_is_c_layout<rq_step_start>);
static_assert(sizeof(rq_step_result) == 48 && k_is_c_layout<rq_step_result>);
static_assert(sizeof(rq_progress) == 72 && k_is_c_layout<rq_progress>);
static_assert(sizeof(rq_error_record) == 112 && k_is_c_layout<rq_error_record>);
static_assert(sizeof(rq_run_result) == 192 && k_is_c_layout<rq_run_result>);

// Every export is noexcept, so no C++ exception can unwind into a host frame. This proves only
// that an escaping exception would terminate the process; ApiBoundaryExceptions throws one
// inside the exports at run time and shows their handlers translate it into RQ_E_INTERNAL.
static_assert(noexcept(rq_api_version()));
static_assert(noexcept(rq_query_system(nullptr)));
static_assert(noexcept(rq_session_create(nullptr, nullptr, nullptr)));
static_assert(noexcept(rq_session_start(nullptr)));
static_assert(noexcept(rq_session_stop(nullptr, RQ_STOP_USER)));
static_assert(noexcept(rq_session_get_result(nullptr, nullptr)));
static_assert(noexcept(rq_session_destroy(nullptr)));
static_assert(noexcept(rq_result_message(RQ_OK)));

// Fixed widths on both sides of the boundary.
static_assert(std::is_same_v<rq_result, std::int32_t>);
static_assert(std::is_same_v<decltype(rq_api_version()), std::uint32_t>);
static_assert(std::is_same_v<decltype(rq_result_message(RQ_OK)), const char*>);
static_assert(RQ_API_VERSION == 1U);
static_assert(RQ_NAME_SIZE == 64 && RQ_END_DETAIL_SIZE == 128);
static_assert(RQ_NO_FIRST_ERROR == UINT64_MAX);

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_bytes_per_mib = std::uint64_t{1} << 20U;

/** Planned bytes of every started session. */
constexpr std::uint64_t k_region_bytes = 64 * k_bytes_per_mib;

/** Shrink-and-retry floor of every started session: half the region. */
constexpr std::uint64_t k_min_region_bytes = 32 * k_bytes_per_mib;

/** Most workers a started session uses; fewer on a machine with one logical processor. */
constexpr std::uint32_t k_max_test_workers = 2;

constexpr std::uint32_t k_detail_cap = 10'000;
constexpr std::uint32_t k_progress_interval_ms = 250;
constexpr std::uint32_t k_step_budget_ms = 300;
constexpr std::uint32_t k_coverage_budget_bp = 2'500;
constexpr std::uint64_t k_seed = 0x5EED'0123'4567'89ABULL;

constexpr std::uint32_t k_step_count = 2;
constexpr char k_step0_name[] = "Random ascending";
constexpr char k_step1_name[] = "Checkerboard descending";

/** Hold of the hold-step shapes the validation table uses. */
constexpr std::uint32_t k_hold_ms = 1'000;

/** Bound of every wait for an engine event. */
constexpr std::chrono::seconds k_event_timeout{30};

/** How long a destroyed session is watched for a late callback. */
constexpr std::chrono::milliseconds k_quiet_period{300};

/** Result-code placeholder for a probe that never ran; positive, so no export returns it. */
constexpr rq_result k_not_called = 1;

/** Every result code ramquick_engine.h defines. */
constexpr rq_result k_known_codes[] = {
    RQ_OK,
    RQ_E_INVALID_ARGUMENT,
    RQ_E_INVALID_HANDLE,
    RQ_E_INVALID_STATE,
    RQ_E_INSUFFICIENT_MEMORY,
    RQ_E_LOCK_FAILED,
    RQ_E_THREAD_CREATE,
    RQ_E_VERSION_MISMATCH,
    RQ_E_AFFINITY_FAILED,
    RQ_E_INTERNAL,
};

/* ---------------------------------------------------------------------------------------------
 * Failure context
 * ------------------------------------------------------------------------------------------- */

/** "code n (message)", so every failure names the code and its rq_result_message text. */
std::string describe(rq_result code) {
    return "code " + std::to_string(code) + " (" + rq_result_message(code) + ")";
}

/** Succeeds when an export returned the expected code; otherwise describes both. */
::testing::AssertionResult returns(rq_result actual, rq_result expected) {
    if (actual == expected) {
        return ::testing::AssertionSuccess();
    }
    return ::testing::AssertionFailure()
           << "returned " << describe(actual) << ", expected " << describe(expected);
}

/** The text of a fixed char array up to its first NUL, or all of it when unterminated. */
template <std::size_t N>
std::string fixed_text(const char (&field)[N]) {
    const void* const nul = std::memchr(field, '\0', N);
    const std::size_t length =
        nul == nullptr ? N : static_cast<std::size_t>(static_cast<const char*>(nul) - field);
    return std::string(field, length);
}

/** True when a fixed char array holds a NUL within its bounds. */
template <std::size_t N>
bool is_terminated(const char (&field)[N]) {
    return std::memchr(field, '\0', N) != nullptr;
}

/** Name generator for parameterised suites whose parameter carries a `name`. */
template <typename Case>
std::string case_name(const ::testing::TestParamInfo<Case>& info) {
    return info.param.name;
}

/* ---------------------------------------------------------------------------------------------
 * Machine facts and configurations
 * ------------------------------------------------------------------------------------------- */

std::uint32_t host_logical_processors() {
    return static_cast<std::uint32_t>(enumerate_logical_processors().size());
}

std::uint32_t test_worker_count() {
    const std::uint32_t processors = host_logical_processors();
    return processors < k_max_test_workers ? processors : k_max_test_workers;
}

/** The host's RQ_ISA_* availability mask, as session creation checks isa_override against it. */
std::uint32_t host_isa_mask() noexcept {
    return select_isa(read_cpu_features(), RQ_ISA_AUTO).available_mask;
}

/** Writes name into step.name, NUL-padded; text beyond RQ_NAME_SIZE - 1 bytes is cut. */
void set_name(rq_step_def& step, const char* name) noexcept {
    std::memset(step.name, 0, sizeof(step.name));
    const std::size_t length = std::strlen(name);
    std::memcpy(step.name, name, length < RQ_NAME_SIZE ? length : RQ_NAME_SIZE - 1);
}

/** A windowed, all-thread step with the test budgets. */
rq_step_def make_step(const char* name, std::uint32_t pattern, std::uint32_t order) noexcept {
    rq_step_def step{};
    set_name(step, name);
    step.pattern = pattern;
    step.access_order = order;
    step.thread_mode = RQ_THREADS_ALL;
    step.hold_ms = 0;
    step.time_budget_ms = k_step_budget_ms;
    step.coverage_budget_bp = k_coverage_budget_bp;
    return step;
}

/** The two-step plan of every started session: about 0.6 s of traffic. */
std::vector<rq_step_def> valid_steps() {
    return {
        make_step(k_step0_name, RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING),
        make_step(k_step1_name, RQ_PATTERN_CHECKERBOARD, RQ_ORDER_SEQUENTIAL_DESCENDING),
    };
}

/** A valid configuration over steps, which must outlive every use of the result. */
rq_session_config valid_config(const std::vector<rq_step_def>& steps) {
    rq_session_config config{};
    config.struct_size = sizeof(rq_session_config);
    config.step_def_size = sizeof(rq_step_def);
    config.region_bytes = k_region_bytes;
    config.min_region_bytes = k_min_region_bytes;
    config.seed = k_seed;
    config.worker_count = test_worker_count();
    config.stop_on_error = 0;
    config.error_detail_cap = k_detail_cap;
    config.progress_interval_ms = k_progress_interval_ms;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = static_cast<std::uint32_t>(steps.size());
    config.steps = steps.data();
    return config;
}

/** The end reason each stop reason maps to. */
std::uint32_t end_reason_for(std::uint32_t stop_reason) noexcept {
    switch (stop_reason) {
    case RQ_STOP_USER:
        return RQ_END_USER_STOP;
    case RQ_STOP_THERMAL:
        return RQ_END_THERMAL;
    case RQ_STOP_HOST_FAULT:
        return RQ_END_HOST_FAULT;
    default:
        return 0;
    }
}

/* ---------------------------------------------------------------------------------------------
 * Callback recorder
 * ------------------------------------------------------------------------------------------- */

/** A structural callback; progress and error batches are counted, not sequenced. */
enum class event_kind { prepared, step_starting, step_finished, completed, released };

struct recorded_event {
    event_kind kind;
    /** Step index for step_starting and step_finished; 0 otherwise. */
    std::uint32_t step_index;
};

/** What the step gate does at one step; every other step continues. */
struct gate_plan {
    /** Step whose gate refuses; UINT32_MAX for none. */
    std::uint32_t abort_step = UINT32_MAX;
    /** Value that gate returns: RQ_ACTION_ABORT, or any other value the engine must treat so. */
    std::uint32_t abort_value = RQ_ACTION_ABORT;
    /** RQ_STOP_* value that gate passes to rq_session_stop before refusing; 0 for none. */
    std::uint32_t stop_reason = 0;
};

/** Everything the callbacks of one session observed, copied under the recorder's lock. */
struct recording {
    std::vector<recorded_event> events;
    std::vector<rq_step_start> step_starts;
    std::vector<rq_step_result> step_results;
    rq_prepared prepared{};
    rq_progress last_progress{};
    rq_run_result completed{};
    /** completed.step_results, copied during on_completed, the only time it is valid. */
    std::vector<rq_step_result> completed_step_results;
    bool released = false;
    std::uint64_t released_bytes = 0;
    std::uint64_t release_ms = 0;

    std::uint32_t progress_count = 0;
    /** Progress raised outside an open step, for another step, or with a wrong struct_size. */
    std::uint32_t misplaced_progress = 0;
    std::uint32_t error_batches = 0;
    /** Error batches whose records pointer is null while count is nonzero. */
    std::uint32_t malformed_error_batches = 0;
    /** total_errors of the latest error batch. */
    std::uint64_t last_total_errors = 0;

    /** Threads callbacks ran on. */
    std::set<DWORD> callback_threads;
    /** A callback body threw; the recorder caught it so nothing unwound into the engine. */
    bool callback_threw = false;

    /** rq_session_destroy from inside each structural callback, when probing. */
    std::vector<rq_result> destroy_in_callback;
    /** rq_session_get_result from inside on_prepared and on_completed, when probing. */
    rq_result get_result_in_prepared = k_not_called;
    rq_result get_result_in_completed = k_not_called;
    /** The header rq_session_get_result copied inside on_completed equals the payload. */
    bool get_result_matches_payload = false;
    /** rq_session_stop from inside the gate or on_prepared, when planned. */
    rq_result stop_in_gate = k_not_called;
    rq_result stop_in_prepared = k_not_called;
};

/**
 * Records the callbacks of one session. Its callbacks() set carries this object as user_data,
 * so it must outlive the session; tests declare it before the session_guard that destroys the
 * session. Configuration setters are called before rq_session_start: the coordinator thread is
 * created after them, which orders their writes before every callback's reads.
 */
class callback_recorder {
public:
    callback_recorder() = default;
    callback_recorder(const callback_recorder&) = delete;
    callback_recorder& operator=(const callback_recorder&) = delete;

    /** A complete callback set over this recorder. */
    rq_callbacks callbacks() noexcept {
        rq_callbacks callback_set{};
        callback_set.struct_size = sizeof(rq_callbacks);
        callback_set.reserved0 = 0;
        callback_set.user_data = this;
        callback_set.on_prepared = &on_prepared;
        callback_set.on_step_starting = &on_step_starting;
        callback_set.on_step_finished = &on_step_finished;
        callback_set.on_progress = &on_progress;
        callback_set.on_errors = &on_errors;
        callback_set.on_completed = &on_completed;
        callback_set.on_released = &on_released;
        return callback_set;
    }

    /** The handle the probes and the planned stops call the API with. */
    void set_handle(rq_session_handle handle) noexcept { handle_ = handle; }
    void set_gate_plan(const gate_plan& plan) noexcept { gate_ = plan; }
    /** Calls rq_session_stop(reason) from inside on_prepared. */
    void stop_in_prepared(std::uint32_t reason) noexcept { prepared_stop_reason_ = reason; }
    /** Calls rq_session_destroy in every structural callback, and get_result in two of them. */
    void probe_from_callbacks() noexcept { probe_ = true; }

    /** Waits until on_released has been raised; false on timeout. */
    bool wait_until_released() {
        return wait_for([](const recording& seen) { return seen.released; });
    }

    /** Waits until the gate of step index has been raised; false on timeout. */
    bool wait_until_step_started(std::uint32_t index) {
        return wait_for([index](const recording& seen) {
            for (const rq_step_start& start : seen.step_starts) {
                if (start.index == index) {
                    return true;
                }
            }
            return false;
        });
    }

    recording snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return data_;
    }

    /** Structural events in order, for example "prepared start0 end0 completed released". */
    std::string trace() const {
        std::lock_guard<std::mutex> lock(mutex_);
        std::string text;
        for (const recorded_event& event : data_.events) {
            if (!text.empty()) {
                text += ' ';
            }
            switch (event.kind) {
            case event_kind::prepared:
                text += "prepared";
                break;
            case event_kind::step_starting:
                text += "start" + std::to_string(event.step_index);
                break;
            case event_kind::step_finished:
                text += "end" + std::to_string(event.step_index);
                break;
            case event_kind::completed:
                text += "completed";
                break;
            case event_kind::released:
                text += "released";
                break;
            }
        }
        return text;
    }

    /** Callbacks of every kind raised so far, progress and error batches included. */
    std::uint64_t callback_count() const noexcept {
        return callback_count_.load(std::memory_order_acquire);
    }

private:
    template <typename Predicate>
    bool wait_for(Predicate done) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, k_event_timeout, [&]() { return done(data_); });
    }

    /**
     * The common path of every callback: counts it, records its thread, runs body under the
     * lock with any exception caught, probes destroy from structural callbacks and wakes the
     * waiters. The engine's callback rules forbid unwinding into its frames.
     */
    template <typename Body>
    void record(bool structural, Body&& body) noexcept {
        callback_count_.fetch_add(1, std::memory_order_acq_rel);
        {
            std::lock_guard<std::mutex> lock(mutex_);
            try {
                data_.callback_threads.insert(GetCurrentThreadId());
                body(data_);
                if (structural && probe_) {
                    data_.destroy_in_callback.push_back(rq_session_destroy(handle_));
                }
            } catch (...) {
                data_.callback_threw = true;
            }
        }
        changed_.notify_all();
    }

    static callback_recorder& self(void* user_data) noexcept {
        return *static_cast<callback_recorder*>(user_data);
    }

    static void RQ_CALL on_prepared(void* user_data, const rq_prepared* info) noexcept {
        callback_recorder& recorder = self(user_data);
        recorder.record(true, [&](recording& seen) {
            seen.prepared = *info;
            seen.events.push_back({event_kind::prepared, 0});
            if (recorder.prepared_stop_reason_ != 0) {
                seen.stop_in_prepared =
                    rq_session_stop(recorder.handle_, recorder.prepared_stop_reason_);
            }
            if (recorder.probe_) {
                rq_run_result result{};
                result.struct_size = sizeof(rq_run_result);
                seen.get_result_in_prepared = rq_session_get_result(recorder.handle_, &result);
            }
        });
    }

    static std::uint32_t RQ_CALL on_step_starting(void* user_data,
                                                  const rq_step_start* info) noexcept {
        callback_recorder& recorder = self(user_data);
        std::uint32_t action = RQ_ACTION_CONTINUE;
        recorder.record(true, [&](recording& seen) {
            seen.step_starts.push_back(*info);
            seen.events.push_back({event_kind::step_starting, info->index});
            if (info->index == recorder.gate_.abort_step) {
                if (recorder.gate_.stop_reason != 0) {
                    seen.stop_in_gate =
                        rq_session_stop(recorder.handle_, recorder.gate_.stop_reason);
                }
                action = recorder.gate_.abort_value;
            } else {
                recorder.open_step_ = static_cast<std::int64_t>(info->index);
            }
        });
        return action;
    }

    static void RQ_CALL on_step_finished(void* user_data, const rq_step_result* result) noexcept {
        callback_recorder& recorder = self(user_data);
        recorder.record(true, [&](recording& seen) {
            seen.step_results.push_back(*result);
            seen.events.push_back({event_kind::step_finished, result->index});
            recorder.open_step_ = -1;
        });
    }

    static void RQ_CALL on_progress(void* user_data, const rq_progress* progress) noexcept {
        callback_recorder& recorder = self(user_data);
        recorder.record(false, [&](recording& seen) {
            ++seen.progress_count;
            seen.last_progress = *progress;
            if (progress->struct_size != sizeof(rq_progress) || recorder.open_step_ < 0 ||
                static_cast<std::int64_t>(progress->step_index) != recorder.open_step_) {
                ++seen.misplaced_progress;
            }
        });
    }

    static void RQ_CALL on_errors(void* user_data, const rq_error_record* records,
                                  std::uint32_t count, std::uint64_t total_errors) noexcept {
        callback_recorder& recorder = self(user_data);
        recorder.record(false, [&](recording& seen) {
            ++seen.error_batches;
            if (count != 0 && records == nullptr) {
                ++seen.malformed_error_batches;
            }
            seen.last_total_errors = total_errors;
        });
    }

    static void RQ_CALL on_completed(void* user_data, const rq_run_result* result) noexcept {
        callback_recorder& recorder = self(user_data);
        recorder.record(true, [&](recording& seen) {
            seen.completed = *result;
            seen.completed_step_results.assign(result->step_results,
                                               result->step_results + result->step_result_count);
            seen.events.push_back({event_kind::completed, 0});
            if (recorder.probe_) {
                rq_run_result copy{};
                copy.struct_size = sizeof(rq_run_result);
                seen.get_result_in_completed = rq_session_get_result(recorder.handle_, &copy);
                seen.get_result_matches_payload =
                    std::memcmp(&copy, result, sizeof(rq_run_result)) == 0;
            }
        });
    }

    static void RQ_CALL on_released(void* user_data, std::uint64_t released_bytes,
                                    std::uint64_t release_ms) noexcept {
        callback_recorder& recorder = self(user_data);
        recorder.record(true, [&](recording& seen) {
            seen.events.push_back({event_kind::released, 0});
            seen.released = true;
            seen.released_bytes = released_bytes;
            seen.release_ms = release_ms;
        });
    }

    // Set before start, read only on the coordinator.
    rq_session_handle handle_ = nullptr;
    gate_plan gate_{};
    std::uint32_t prepared_stop_reason_ = 0;
    bool probe_ = false;

    // Coordinator only: the step whose gate continued and whose on_step_finished is pending.
    std::int64_t open_step_ = -1;

    mutable std::mutex mutex_;
    std::condition_variable changed_;
    recording data_;
    std::atomic<std::uint64_t> callback_count_{0};
};

/* ---------------------------------------------------------------------------------------------
 * Session ownership
 * ------------------------------------------------------------------------------------------- */

/** Owns one session handle and destroys it on every exit path, a failed assertion included. */
class session_guard {
public:
    session_guard() = default;
    session_guard(const session_guard&) = delete;
    session_guard& operator=(const session_guard&) = delete;

    ~session_guard() {
        if (handle_ != nullptr) {
            static_cast<void>(rq_session_destroy(handle_));
        }
    }

    rq_session_handle get() const noexcept { return handle_; }

    /** Out parameter for rq_session_create. */
    rq_session_handle* out() noexcept { return &handle_; }

    /** rq_session_destroy; the guard lets the handle go once the engine accepted the call. */
    rq_result destroy() noexcept {
        const rq_result code = rq_session_destroy(handle_);
        if (code == RQ_OK) {
            handle_ = nullptr;
        }
        return code;
    }

private:
    rq_session_handle handle_ = nullptr;
};

/** Creates a session over recorder, gives the recorder its handle and starts it. */
::testing::AssertionResult create_and_start(const rq_session_config& config,
                                            callback_recorder& recorder, session_guard& session) {
    const rq_callbacks callbacks = recorder.callbacks();
    const rq_result created = rq_session_create(&config, &callbacks, session.out());
    if (created != RQ_OK) {
        return ::testing::AssertionFailure() << "rq_session_create " << describe(created);
    }
    recorder.set_handle(session.get());
    const rq_result started = rq_session_start(session.get());
    if (started != RQ_OK) {
        return ::testing::AssertionFailure() << "rq_session_start " << describe(started);
    }
    return ::testing::AssertionSuccess();
}

/** Asserts that no session outlives its test: every handle a test creates is destroyed. */
class ApiBoundarySession : public ::testing::Test {
protected:
    void SetUp() override { baseline_sessions_ = live_session_count(); }

    void TearDown() override {
        EXPECT_EQ(live_session_count(), baseline_sessions_) << "a session was not destroyed";
    }

    std::uint32_t baseline_sessions_ = 0;
};

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryAbi: layout, version and result messages
 * ------------------------------------------------------------------------------------------- */

TEST(ApiBoundaryAbi, StructSizesMatchTheDocumentedLayout) {
    // Also asserted at compile time above; the runtime check names the struct on failure.
    EXPECT_EQ(sizeof(rq_system_info), 112U);
    EXPECT_EQ(sizeof(rq_step_def), 96U);
    EXPECT_EQ(sizeof(rq_session_config), 64U);
    EXPECT_EQ(sizeof(rq_callbacks), 72U);
    EXPECT_EQ(sizeof(rq_prepared), 40U);
    EXPECT_EQ(sizeof(rq_step_start), 80U);
    EXPECT_EQ(sizeof(rq_step_result), 48U);
    EXPECT_EQ(sizeof(rq_progress), 72U);
    EXPECT_EQ(sizeof(rq_error_record), 112U);
    EXPECT_EQ(sizeof(rq_run_result), 192U);
}

TEST(ApiBoundaryAbi, VersionIsTheHeaderConstantOnEveryThread) {
    // A host compares this with the RQ_API_VERSION it was built against and refuses to bind on
    // a mismatch (RQ_E_VERSION_MISMATCH, raised by the wrapper, never by the engine).
    EXPECT_EQ(rq_api_version(), RQ_API_VERSION);
    EXPECT_EQ(rq_api_version(), 1U);

    std::uint32_t other_thread_version = 0;
    std::thread reader([&other_thread_version]() { other_thread_version = rq_api_version(); });
    reader.join();
    EXPECT_EQ(other_thread_version, RQ_API_VERSION);
}

TEST(ApiBoundaryAbi, ResultMessagesAreDistinctForKnownCodesAndFixedForUnknownOnes) {
    std::set<std::string> texts;
    for (const rq_result code : k_known_codes) {
        const char* const message = rq_result_message(code);
        ASSERT_NE(message, nullptr) << "code " << code;
        EXPECT_GT(std::strlen(message), 0U) << "code " << code;
        EXPECT_NE(std::string(message), "Unknown result code") << "code " << code;
        texts.insert(message);
    }
    EXPECT_EQ(texts.size(), std::size(k_known_codes)) << "two known codes share a message";

    for (const rq_result code : {rq_result{-50}, rq_result{1}, rq_result{42}, INT32_MIN}) {
        const char* const message = rq_result_message(code);
        ASSERT_NE(message, nullptr) << "code " << code;
        EXPECT_STREQ(message, "Unknown result code") << "code " << code;
    }
}

/* ---------------------------------------------------------------------------------------------
 * ApiBoundarySystem: rq_query_system
 * ------------------------------------------------------------------------------------------- */

TEST(ApiBoundarySystem, RejectsANullOrMisSizedStructWithoutWritingIt) {
    EXPECT_TRUE(returns(rq_query_system(nullptr), RQ_E_INVALID_ARGUMENT));

    constexpr std::uint32_t k_wrong_sizes[] = {0U, sizeof(rq_system_info) - 8U,
                                               sizeof(rq_system_info) + 8U, UINT32_MAX};
    for (const std::uint32_t size : k_wrong_sizes) {
        rq_system_info info{};
        std::memset(&info, 0xA5, sizeof(info));
        info.struct_size = size;
        const rq_system_info untouched = info;

        EXPECT_TRUE(returns(rq_query_system(&info), RQ_E_INVALID_ARGUMENT))
            << "struct_size " << size;
        EXPECT_EQ(std::memcmp(&info, &untouched, sizeof(info)), 0)
            << "struct_size " << size << ": the struct was written";
    }
}

TEST(ApiBoundarySystem, DescribesThisMachine) {
    rq_system_info info{};
    info.struct_size = sizeof(rq_system_info);
    ASSERT_TRUE(returns(rq_query_system(&info), RQ_OK));

    EXPECT_EQ(info.struct_size, sizeof(rq_system_info)) << "struct_size is the caller's";

    const std::vector<logical_processor> processors = enumerate_logical_processors();
    EXPECT_GE(info.logical_processors, 1U);
    EXPECT_EQ(info.logical_processors, processors.size());
    std::set<std::uint16_t> groups;
    for (const logical_processor& processor : processors) {
        groups.insert(processor.group);
    }
    EXPECT_GE(info.processor_groups, 1U);
    EXPECT_GE(info.processor_groups, groups.size()) << "fewer groups than hold processors";

    EXPECT_GT(info.avail_phys_bytes, 0U);
    EXPECT_GE(info.total_phys_bytes, info.avail_phys_bytes);
    EXPECT_TRUE(info.large_page_min_bytes == 0U ||
                (info.large_page_min_bytes & (info.large_page_min_bytes - 1U)) == 0U)
        << "large-page granularity " << info.large_page_min_bytes << " is not a power of two";

    // The instruction sets are reported exactly as session creation will check isa_override.
    const isa_selection host = select_isa(read_cpu_features(), RQ_ISA_AUTO);
    EXPECT_NE(info.isa_available_mask & (1U << RQ_ISA_SSE2), 0U) << "SSE2 is architectural";
    EXPECT_EQ(info.isa_available_mask & (1U << RQ_ISA_AUTO), 0U) << "AUTO is not a set";
    EXPECT_EQ(info.isa_available_mask >> (RQ_ISA_AVX512 + 1U), 0U) << "bit beyond AVX-512";
    EXPECT_NE(info.isa_best, RQ_ISA_AUTO);
    EXPECT_NE(info.isa_available_mask & (1U << info.isa_best), 0U) << "best is not available";
    EXPECT_EQ(info.isa_available_mask, host.available_mask);
    EXPECT_EQ(info.isa_best, host.best);

    EXPECT_TRUE(info.lock_privilege_held == 0U || info.lock_privilege_held == 1U);
    EXPECT_EQ(info.lock_privilege_held != 0U, lock_memory_privilege_held());
    EXPECT_TRUE(is_terminated(info.cpu_name)) << "cpu_name is not NUL-terminated in 64 bytes";
}

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryCreateInvalid and ApiBoundaryCreateValid: rq_session_create validation
 * ------------------------------------------------------------------------------------------- */

/** The arguments of one rq_session_create call; a case mutates a valid set. */
struct create_arguments {
    std::vector<rq_step_def> steps;
    rq_session_config config{};
    rq_callbacks callbacks{};
    bool pass_config = true;
    bool pass_callbacks = true;
    bool pass_out = true;
};

/** A named mutation of the valid arguments. */
struct create_case {
    const char* name;
    void (*mutate)(create_arguments&);
};

/**
 * Each case breaks the rule it names while every other field stays valid. HoldWithTurnaround
 * necessarily also lacks the random-block order every hold step requires.
 */
const create_case k_invalid_create_cases[] = {
    {"NullConfig", [](create_arguments& a) { a.pass_config = false; }},
    {"NullCallbacks", [](create_arguments& a) { a.pass_callbacks = false; }},
    {"NullOut", [](create_arguments& a) { a.pass_out = false; }},
    {"ConfigStructSizeZero", [](create_arguments& a) { a.config.struct_size = 0; }},
    {"ConfigStructSizeShort",
     [](create_arguments& a) { a.config.struct_size = sizeof(rq_session_config) - 8U; }},
    {"ConfigStructSizeLong",
     [](create_arguments& a) { a.config.struct_size = sizeof(rq_session_config) + 8U; }},
    {"CallbacksStructSizeShort",
     [](create_arguments& a) { a.callbacks.struct_size = sizeof(rq_callbacks) - 8U; }},
    {"CallbacksStructSizeLong",
     [](create_arguments& a) { a.callbacks.struct_size = sizeof(rq_callbacks) + 8U; }},
    {"StepDefSizeZero", [](create_arguments& a) { a.config.step_def_size = 0; }},
    {"StepDefSizeShort",
     [](create_arguments& a) { a.config.step_def_size = sizeof(rq_step_def) - 1U; }},
    {"StepDefSizeLong",
     [](create_arguments& a) { a.config.step_def_size = sizeof(rq_step_def) + 8U; }},
    {"StepCountZeroWithSteps", [](create_arguments& a) { a.config.step_count = 0; }},
    {"StepCountZeroWithoutSteps",
     [](create_arguments& a) {
         a.config.step_count = 0;
         a.config.steps = nullptr;
     }},
    {"StepsNullWithCount", [](create_arguments& a) { a.config.steps = nullptr; }},
    {"StepCountAboveMaximum",
     [](create_arguments& a) { a.config.step_count = k_max_step_count + 1U; }},
    {"NameEmpty", [](create_arguments& a) { set_name(a.steps[0], ""); }},
    {"NameUnterminated",
     [](create_arguments& a) { std::memset(a.steps[1].name, 'N', sizeof(a.steps[1].name)); }},
    {"NameInvalidUtf8", [](create_arguments& a) { set_name(a.steps[0], "Bad \xFF byte"); }},
    {"NameOverlongUtf8", [](create_arguments& a) { set_name(a.steps[0], "Slash \xC0\xAF"); }},
    {"NameSurrogateUtf8", [](create_arguments& a) { set_name(a.steps[0], "Half \xED\xA0\x80"); }},
    {"NameDuplicate", [](create_arguments& a) { set_name(a.steps[1], k_step0_name); }},
    {"PatternZero", [](create_arguments& a) { a.steps[0].pattern = 0; }},
    {"PatternSix", [](create_arguments& a) { a.steps[0].pattern = RQ_PATTERN_HIGH_TOGGLE + 1U; }},
    {"OrderZero", [](create_arguments& a) { a.steps[0].access_order = 0; }},
    {"OrderSix", [](create_arguments& a) { a.steps[0].access_order = RQ_ORDER_TURNAROUND + 1U; }},
    {"ThreadModeZero", [](create_arguments& a) { a.steps[0].thread_mode = 0; }},
    {"ThreadModeThree", [](create_arguments& a) { a.steps[0].thread_mode = RQ_THREADS_HALF + 1U; }},
    {"TimeBudgetZero", [](create_arguments& a) { a.steps[1].time_budget_ms = 0; }},
    {"CoverageBudgetZero", [](create_arguments& a) { a.steps[1].coverage_budget_bp = 0; }},
    {"HoldWithSequentialOrder", [](create_arguments& a) { a.steps[0].hold_ms = k_hold_ms; }},
    {"HoldWithTurnaround",
     [](create_arguments& a) {
         a.steps[0].access_order = RQ_ORDER_TURNAROUND;
         a.steps[0].hold_ms = k_hold_ms;
     }},
    {"HoldWithMovingInversionWalking",
     [](create_arguments& a) {
         a.steps[0].pattern = RQ_PATTERN_MOVING_INVERSION_WALKING;
         a.steps[0].access_order = RQ_ORDER_RANDOM_BLOCKS;
         a.steps[0].hold_ms = k_hold_ms;
     }},
    {"HoldWithMovingInversionRandom",
     [](create_arguments& a) {
         a.steps[0].pattern = RQ_PATTERN_MOVING_INVERSION_RANDOM;
         a.steps[0].access_order = RQ_ORDER_RANDOM_BLOCKS;
         a.steps[0].hold_ms = k_hold_ms;
     }},
    {"RegionBelowMinimum",
     [](create_arguments& a) { a.config.region_bytes = a.config.min_region_bytes - 1U; }},
    {"MinimumRegionZero", [](create_arguments& a) { a.config.min_region_bytes = 0; }},
    {"WorkerCountZero", [](create_arguments& a) { a.config.worker_count = 0; }},
    {"WorkerCountAboveLogicalProcessors",
     [](create_arguments& a) {
         a.config.worker_count = host_logical_processors() + 1U;
         a.config.min_region_bytes = std::uint64_t{a.config.worker_count} * k_min_slice_bytes;
         a.config.region_bytes = a.config.min_region_bytes;
     }},
    {"MinimumSliceBelowTwoMiB",
     [](create_arguments& a) {
         a.config.min_region_bytes = std::uint64_t{a.config.worker_count} * k_min_slice_bytes - 1U;
     }},
    {"StopOnErrorTwo", [](create_arguments& a) { a.config.stop_on_error = 2; }},
    {"DetailCapAboveMaximum",
     [](create_arguments& a) { a.config.error_detail_cap = k_max_error_detail_cap + 1U; }},
    {"ProgressIntervalZero", [](create_arguments& a) { a.config.progress_interval_ms = 0; }},
    {"IsaOverrideFour", [](create_arguments& a) { a.config.isa_override = RQ_ISA_AVX512 + 1U; }},
    {"IsaOverrideMaximum", [](create_arguments& a) { a.config.isa_override = UINT32_MAX; }},
    {"NullOnPrepared", [](create_arguments& a) { a.callbacks.on_prepared = nullptr; }},
    {"NullOnStepStarting", [](create_arguments& a) { a.callbacks.on_step_starting = nullptr; }},
    {"NullOnStepFinished", [](create_arguments& a) { a.callbacks.on_step_finished = nullptr; }},
    {"NullOnProgress", [](create_arguments& a) { a.callbacks.on_progress = nullptr; }},
    {"NullOnErrors", [](create_arguments& a) { a.callbacks.on_errors = nullptr; }},
    {"NullOnCompleted", [](create_arguments& a) { a.callbacks.on_completed = nullptr; }},
    {"NullOnReleased", [](create_arguments& a) { a.callbacks.on_released = nullptr; }},
};

/** Configurations at the limits of the rules, which creation must accept. */
const create_case k_valid_create_cases[] = {
    {"TheTestPlan", [](create_arguments&) {}},
    {"NameOfSixtyThreeBytes",
     [](create_arguments& a) { set_name(a.steps[0], std::string(RQ_NAME_SIZE - 1, 'N').c_str()); }},
    // Two-, three- and four-byte sequences; the literal is split where a letter would extend a
    // hex escape.
    {"MultibyteUtf8Name",
     [](create_arguments& a) {
         set_name(a.steps[0], "Pr\xC3\xBC"
                              "fung \xE2\x80\x94 \xF0\x9F\x98\x80");
     }},
    {"HoldStepInRandomBlockOrder",
     [](create_arguments& a) {
         a.steps[0].access_order = RQ_ORDER_RANDOM_BLOCKS;
         a.steps[0].hold_ms = k_hold_ms;
     }},
    {"HalfThreadMode", [](create_arguments& a) { a.steps[1].thread_mode = RQ_THREADS_HALF; }},
    {"MovingInversionWithAnyOrder",
     [](create_arguments& a) {
         a.steps[0].pattern = RQ_PATTERN_MOVING_INVERSION_WALKING;
         a.steps[0].access_order = RQ_ORDER_TURNAROUND;
     }},
    {"RegionEqualsMinimum",
     [](create_arguments& a) { a.config.region_bytes = a.config.min_region_bytes; }},
    {"MinimumSliceOfTwoMiB",
     [](create_arguments& a) {
         a.config.min_region_bytes = std::uint64_t{a.config.worker_count} * k_min_slice_bytes;
     }},
    {"WorkersOnEveryLogicalProcessor",
     [](create_arguments& a) {
         a.config.worker_count = host_logical_processors();
         a.config.min_region_bytes = std::uint64_t{a.config.worker_count} * k_min_slice_bytes;
         a.config.region_bytes = a.config.min_region_bytes;
     }},
    {"DetailCapZero", [](create_arguments& a) { a.config.error_detail_cap = 0; }},
    {"DetailCapMaximum",
     [](create_arguments& a) { a.config.error_detail_cap = k_max_error_detail_cap; }},
    {"StopOnErrorSet", [](create_arguments& a) { a.config.stop_on_error = 1; }},
    {"ForcedSse2", [](create_arguments& a) { a.config.isa_override = RQ_ISA_SSE2; }},
    {"AnySeed", [](create_arguments& a) { a.config.seed = UINT64_MAX; }},
    {"MostSteps",
     [](create_arguments& a) {
         const rq_step_def first = a.steps[0];
         a.steps.assign(k_max_step_count, first);
         for (std::uint32_t i = 0; i < k_max_step_count; ++i) {
             set_name(a.steps[i], ("Step " + std::to_string(i)).c_str());
         }
         a.config.steps = a.steps.data();
         a.config.step_count = k_max_step_count;
     }},
};

/** Valid arguments over recorder's callbacks, before a case mutates them. */
create_arguments make_create_arguments(callback_recorder& recorder) {
    create_arguments arguments;
    arguments.steps = valid_steps();
    arguments.config = valid_config(arguments.steps);
    arguments.callbacks = recorder.callbacks();
    return arguments;
}

/** rq_session_create with the arguments a case left, into handle. */
rq_result create_with(create_arguments& arguments, rq_session_handle* handle) {
    return rq_session_create(arguments.pass_config ? &arguments.config : nullptr,
                             arguments.pass_callbacks ? &arguments.callbacks : nullptr,
                             arguments.pass_out ? handle : nullptr);
}

class ApiBoundaryCreateInvalid : public ApiBoundarySession,
                                 public ::testing::WithParamInterface<create_case> {};

TEST_P(ApiBoundaryCreateInvalid, IsRejectedWithoutRegisteringASession) {
    callback_recorder recorder;
    create_arguments arguments = make_create_arguments(recorder);
    GetParam().mutate(arguments);

    // A non-null sentinel shows that a failed create clears the out parameter.
    rq_session_handle handle = reinterpret_cast<rq_session_handle>(std::uintptr_t{0x1000});
    const std::uint32_t before = live_session_count();
    const rq_result code = create_with(arguments, &handle);

    EXPECT_TRUE(returns(code, RQ_E_INVALID_ARGUMENT));
    EXPECT_EQ(live_session_count(), before) << "a rejected configuration registered a session";
    if (arguments.pass_out) {
        EXPECT_EQ(handle, nullptr) << "a rejected configuration left a handle";
    }
    if (code == RQ_OK) {
        static_cast<void>(rq_session_destroy(handle));
    }
    EXPECT_EQ(recorder.callback_count(), 0U);
}

INSTANTIATE_TEST_SUITE_P(ApiBoundaryEveryRule, ApiBoundaryCreateInvalid,
                         ::testing::ValuesIn(k_invalid_create_cases), case_name<create_case>);

class ApiBoundaryCreateValid : public ApiBoundarySession,
                               public ::testing::WithParamInterface<create_case> {};

TEST_P(ApiBoundaryCreateValid, IsAcceptedAndRegisteredUntilDestroyed) {
    callback_recorder recorder;
    create_arguments arguments = make_create_arguments(recorder);
    GetParam().mutate(arguments);

    session_guard session;
    const std::uint32_t before = live_session_count();
    ASSERT_TRUE(returns(create_with(arguments, session.out()), RQ_OK));
    EXPECT_NE(session.get(), nullptr);
    EXPECT_EQ(live_session_count(), before + 1U);

    // Creation starts no thread, so nothing is raised before start.
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
    EXPECT_EQ(live_session_count(), before);
    EXPECT_EQ(recorder.callback_count(), 0U);
}

INSTANTIATE_TEST_SUITE_P(ApiBoundaryAtTheLimits, ApiBoundaryCreateValid,
                         ::testing::ValuesIn(k_valid_create_cases), case_name<create_case>);

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryHandle: handles and session state
 * ------------------------------------------------------------------------------------------- */

class ApiBoundaryHandle : public ApiBoundarySession {};

/** Expects every handle-taking export to reject handle with RQ_E_INVALID_HANDLE. */
void expect_handle_rejected(rq_session_handle handle) {
    rq_run_result result{};
    result.struct_size = sizeof(rq_run_result);
    const void* const value = handle;
    EXPECT_TRUE(returns(rq_session_start(handle), RQ_E_INVALID_HANDLE)) << "start " << value;
    EXPECT_TRUE(returns(rq_session_stop(handle, RQ_STOP_USER), RQ_E_INVALID_HANDLE))
        << "stop " << value;
    EXPECT_TRUE(returns(rq_session_get_result(handle, &result), RQ_E_INVALID_HANDLE))
        << "get_result " << value;
    EXPECT_TRUE(returns(rq_session_destroy(handle), RQ_E_INVALID_HANDLE)) << "destroy " << value;
}

TEST_F(ApiBoundaryHandle, NeverIssuedHandlesAreRejectedWithoutBeingDereferenced) {
    // The registry compares values only, so none of these is ever read through.
    static int unrelated_object = 0;
    expect_handle_rejected(nullptr);
    expect_handle_rejected(reinterpret_cast<rq_session_handle>(std::uintptr_t{0x1000}));
    expect_handle_rejected(reinterpret_cast<rq_session_handle>(~std::uintptr_t{0xFFF}));
    expect_handle_rejected(reinterpret_cast<rq_session_handle>(&unrelated_object));
}

TEST_F(ApiBoundaryHandle, DestroyedHandleIsRejected) {
    callback_recorder recorder;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    const rq_callbacks callbacks = recorder.callbacks();
    session_guard session;
    ASSERT_TRUE(returns(rq_session_create(&config, &callbacks, session.out()), RQ_OK));

    const rq_session_handle stale = session.get();
    ASSERT_TRUE(returns(session.destroy(), RQ_OK));
    expect_handle_rejected(stale);
    EXPECT_EQ(recorder.callback_count(), 0U);
}

TEST_F(ApiBoundaryHandle, UnknownStopReasonsAreRejectedAndRecordNothing) {
    callback_recorder recorder;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    const rq_callbacks callbacks = recorder.callbacks();
    session_guard session;
    ASSERT_TRUE(returns(rq_session_create(&config, &callbacks, session.out()), RQ_OK));
    recorder.set_handle(session.get());

    for (const std::uint32_t reason : {0U, RQ_STOP_HOST_FAULT + 1U, UINT32_MAX}) {
        EXPECT_TRUE(returns(rq_session_stop(session.get(), reason), RQ_E_INVALID_ARGUMENT))
            << "reason " << reason;
        // The reason is checked before the handle.
        EXPECT_TRUE(returns(rq_session_stop(nullptr, reason), RQ_E_INVALID_ARGUMENT))
            << "reason " << reason << " on a null handle";
    }

    // Had a rejected call recorded anything, it would win over this first valid reason.
    ASSERT_TRUE(returns(rq_session_stop(session.get(), RQ_STOP_THERMAL), RQ_OK));
    ASSERT_TRUE(returns(rq_session_start(session.get()), RQ_OK));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();
    EXPECT_EQ(recorder.snapshot().completed.end_reason, RQ_END_THERMAL);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

TEST_F(ApiBoundaryHandle, GetResultChecksItsOutParameterBeforeTheHandle) {
    callback_recorder recorder;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    const rq_callbacks callbacks = recorder.callbacks();
    session_guard session;
    ASSERT_TRUE(returns(rq_session_create(&config, &callbacks, session.out()), RQ_OK));

    for (const rq_session_handle handle : {session.get(), rq_session_handle{nullptr}}) {
        EXPECT_TRUE(returns(rq_session_get_result(handle, nullptr), RQ_E_INVALID_ARGUMENT));
        for (const std::uint32_t size :
             {0U, static_cast<std::uint32_t>(sizeof(rq_run_result) - 8U)}) {
            rq_run_result result{};
            std::memset(&result, 0xA5, sizeof(result));
            result.struct_size = size;
            const rq_run_result untouched = result;
            EXPECT_TRUE(returns(rq_session_get_result(handle, &result), RQ_E_INVALID_ARGUMENT))
                << "struct_size " << size;
            EXPECT_EQ(std::memcmp(&result, &untouched, sizeof(result)), 0)
                << "struct_size " << size << ": the struct was written";
        }
    }
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

TEST_F(ApiBoundaryHandle, StartIsAllowedOnceAndTheResultOnlyAfterCompleted) {
    callback_recorder recorder;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    const rq_callbacks callbacks = recorder.callbacks();
    session_guard session;
    ASSERT_TRUE(returns(rq_session_create(&config, &callbacks, session.out()), RQ_OK));
    recorder.set_handle(session.get());

    rq_run_result result{};
    result.struct_size = sizeof(rq_run_result);
    EXPECT_TRUE(returns(rq_session_get_result(session.get(), &result), RQ_E_INVALID_STATE))
        << "before start";

    // A stop recorded before start makes the run end at once, so both starts are deterministic.
    ASSERT_TRUE(returns(rq_session_stop(session.get(), RQ_STOP_USER), RQ_OK));
    ASSERT_TRUE(returns(rq_session_start(session.get()), RQ_OK));
    EXPECT_TRUE(returns(rq_session_start(session.get()), RQ_E_INVALID_STATE)) << "second start";
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();
    EXPECT_TRUE(returns(rq_session_start(session.get()), RQ_E_INVALID_STATE))
        << "start after completion";

    ASSERT_TRUE(returns(rq_session_get_result(session.get(), &result), RQ_OK));
    EXPECT_EQ(result.end_reason, RQ_END_USER_STOP);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryExceptions: C++ exceptions inside the exports
 *
 * rq_session_start is not swept: its only allocation, std::thread's 8-byte start state, is
 * below k_refusable_bytes, so no refusal can reach it.
 * ------------------------------------------------------------------------------------------- */

/**
 * Arms the allocation refusal on the constructing thread until destruction: the first
 * pass_through refusable requests succeed and every later one throws std::bad_alloc. GoogleTest
 * allocates, so no assertion may run while one is alive; tests capture the export's result and
 * refused() inside the guard's scope and assert after it.
 */
class allocation_refusal {
public:
    explicit allocation_refusal(std::uint32_t pass_through = 0) noexcept {
        refusal_state = {true, pass_through, 0};
    }
    allocation_refusal(const allocation_refusal&) = delete;
    allocation_refusal& operator=(const allocation_refusal&) = delete;
    ~allocation_refusal() { refusal_state.armed = false; }

    /** Requests refused on this thread since construction. */
    std::uint32_t refused() const noexcept { return refusal_state.refused; }
};

/** Refusal points the create sweep tries; far more than session creation allocates. */
constexpr std::uint32_t k_max_create_refusal_points = 256;

class ApiBoundaryExceptions : public ApiBoundarySession {};

TEST_F(ApiBoundaryExceptions, QuerySystemTranslatesAThrownExceptionAndLeavesTheStructUnwritten) {
    rq_system_info info{};
    std::memset(&info, 0xA5, sizeof(info));
    info.struct_size = sizeof(rq_system_info);
    const rq_system_info untouched = info;

    rq_result code = k_not_called;
    std::uint32_t refused = 0;
    {
        const allocation_refusal refusal;
        code = rq_query_system(&info);
        refused = refusal.refused();
    }

    EXPECT_GE(refused, 1U) << "rq_query_system made no refusable allocation, so nothing threw";
    EXPECT_TRUE(returns(code, RQ_E_INTERNAL));
    // query_system fills a local and copies it once, after its last throwing call.
    EXPECT_EQ(std::memcmp(&info, &untouched, sizeof(info)), 0) << "the struct was written";

    rq_system_info after{};
    after.struct_size = sizeof(rq_system_info);
    EXPECT_TRUE(returns(rq_query_system(&after), RQ_OK)) << "unarmed, after the refusal";
}

TEST_F(ApiBoundaryExceptions, CreateTranslatesAThrowAtEveryAllocationAndRegistersNothing) {
    callback_recorder recorder;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    const rq_callbacks callbacks = recorder.callbacks();

    // Pass n lets the first n refusable allocations of the call through and throws at the next,
    // so the sweep reaches every allocation creation makes; the first pass that refuses nothing
    // is a complete, successful creation and ends it.
    std::uint32_t failing_points = 0;
    bool created_unrefused = false;
    for (std::uint32_t pass = 0; pass < k_max_create_refusal_points && !created_unrefused; ++pass) {
        // A non-null sentinel shows that a translated exception leaves no stale handle.
        rq_session_handle handle = reinterpret_cast<rq_session_handle>(std::uintptr_t{0x1000});
        const std::uint32_t before = live_session_count();
        rq_result code = k_not_called;
        std::uint32_t refused = 0;
        {
            const allocation_refusal refusal(pass);
            code = rq_session_create(&config, &callbacks, &handle);
            refused = refusal.refused();
        }

        if (refused == 0) {
            created_unrefused = true;
            EXPECT_TRUE(returns(code, RQ_OK)) << "pass " << pass << ": nothing was refused";
            EXPECT_NE(handle, nullptr) << "pass " << pass;
            EXPECT_EQ(live_session_count(), before + 1U) << "pass " << pass;
            if (code == RQ_OK) {
                EXPECT_TRUE(returns(rq_session_destroy(handle), RQ_OK)) << "pass " << pass;
            }
            continue;
        }

        ++failing_points;
        EXPECT_TRUE(returns(code, RQ_E_INTERNAL))
            << "pass " << pass << ": " << refused << " allocation(s) refused";
        EXPECT_EQ(handle, nullptr) << "pass " << pass << ": a translated exception left a handle";
        EXPECT_EQ(live_session_count(), before)
            << "pass " << pass << ": a failed creation registered a session";
        if (code == RQ_OK) {
            // Already reported above; the session must not outlive the test.
            static_cast<void>(rq_session_destroy(handle));
        }
    }

    EXPECT_TRUE(created_unrefused)
        << "creation still allocated after " << k_max_create_refusal_points << " refusal points";
    EXPECT_GE(failing_points, 1U) << "creation made no refusable allocation, so nothing threw";
    EXPECT_EQ(recorder.callback_count(), 0U);
}

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryRun: started sessions
 * ------------------------------------------------------------------------------------------- */

/** The structural trace of a run in which both steps ran to their schedule. */
constexpr char k_complete_trace[] = "prepared start0 end0 start1 end1 completed released";

class ApiBoundaryRun : public ApiBoundarySession {};

TEST_F(ApiBoundaryRun, NormalRunFollowsTheDocumentedEventOrderAndPayloads) {
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    callback_recorder recorder;
    session_guard session;
    ASSERT_TRUE(create_and_start(config, recorder, session));
    EXPECT_TRUE(returns(rq_session_start(session.get()), RQ_E_INVALID_STATE)) << "second start";
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    EXPECT_EQ(recorder.trace(), k_complete_trace);
    const recording seen = recorder.snapshot();
    EXPECT_FALSE(seen.callback_threw);

    // Every callback ran on the one coordinator thread, never on the host's.
    ASSERT_EQ(seen.callback_threads.size(), 1U);
    EXPECT_NE(*seen.callback_threads.begin(), GetCurrentThreadId());

    const rq_prepared& prepared = seen.prepared;
    EXPECT_EQ(prepared.struct_size, sizeof(rq_prepared));
    EXPECT_EQ(prepared.worker_count, config.worker_count);
    EXPECT_EQ(prepared.isa_selected, select_isa(read_cpu_features(), RQ_ISA_AUTO).best);
    EXPECT_EQ(prepared.address_kind, RQ_ADDRESS_KIND_VIRTUAL);
    EXPECT_GE(prepared.page_mode, RQ_PAGE_MODE_STANDARD);
    EXPECT_LE(prepared.page_mode, RQ_PAGE_MODE_MIXED);
    EXPECT_EQ(prepared.reserved0, 0U);
    EXPECT_GE(prepared.tested_bytes, config.min_region_bytes);
    EXPECT_LE(prepared.tested_bytes, config.region_bytes);
    EXPECT_EQ(prepared.tested_bytes % (std::uint64_t{config.worker_count} * k_min_slice_bytes), 0U)
        << "slices are not whole 2 MiB multiples";

    const char* const names[k_step_count] = {k_step0_name, k_step1_name};
    ASSERT_EQ(seen.step_starts.size(), k_step_count);
    for (std::uint32_t i = 0; i < k_step_count; ++i) {
        const rq_step_start& start = seen.step_starts[i];
        EXPECT_EQ(start.struct_size, sizeof(rq_step_start)) << "step " << i;
        EXPECT_EQ(start.index, i);
        EXPECT_EQ(start.count, k_step_count) << "step " << i;
        EXPECT_EQ(start.reserved0, 0U) << "step " << i;
        EXPECT_TRUE(is_terminated(start.name)) << "step " << i;
        EXPECT_EQ(fixed_text(start.name), names[i]);
    }

    EXPECT_GE(seen.progress_count, 1U);
    EXPECT_EQ(seen.misplaced_progress, 0U) << "progress outside its step or mis-sized";
    EXPECT_EQ(seen.last_progress.step_count, k_step_count);
    EXPECT_EQ(seen.last_progress.region_bytes, prepared.tested_bytes);
    EXPECT_EQ(seen.malformed_error_batches, 0U);

    ASSERT_EQ(seen.step_results.size(), k_step_count);
    std::uint64_t verified_bytes = 0;
    std::uint64_t errors = 0;
    for (std::uint32_t i = 0; i < k_step_count; ++i) {
        const rq_step_result& step = seen.step_results[i];
        EXPECT_EQ(step.index, i);
        EXPECT_EQ(step.completed, 1U) << "step " << i;
        EXPECT_GE(step.passes, 1U) << "step " << i;
        EXPECT_GT(step.verified_bytes, 0U) << "step " << i;
        EXPECT_EQ(step.reserved0, 0U) << "step " << i;
        verified_bytes += step.verified_bytes;
        errors += step.errors;
    }

    const rq_run_result& result = seen.completed;
    EXPECT_EQ(result.struct_size, sizeof(rq_run_result));
    EXPECT_EQ(result.end_reason, RQ_END_COMPLETED);
    EXPECT_EQ(result.steps_completed, k_step_count);
    EXPECT_EQ(result.step_result_count, k_step_count);
    EXPECT_EQ(result.verified_bytes, verified_bytes);
    EXPECT_EQ(result.total_errors, errors);
    if (result.total_errors == 0U) {
        EXPECT_EQ(result.first_error_ms, RQ_NO_FIRST_ERROR);
    } else {
        EXPECT_LE(result.first_error_ms, result.elapsed_ms);
    }
    // Diagnostics are reserved for the other end reasons.
    EXPECT_EQ(result.os_error, 0U);
    EXPECT_EQ(fixed_text(result.end_detail), "");
    ASSERT_EQ(seen.completed_step_results.size(), k_step_count);
    for (std::uint32_t i = 0; i < k_step_count; ++i) {
        EXPECT_EQ(std::memcmp(&seen.completed_step_results[i], &seen.step_results[i],
                              sizeof(rq_step_result)),
                  0)
            << "step " << i << " differs between on_step_finished and on_completed";
    }
    EXPECT_EQ(seen.released_bytes, prepared.tested_bytes);

    // After Completed the header is available, and its step results stay valid until destroy.
    rq_run_result copy{};
    copy.struct_size = sizeof(rq_run_result);
    ASSERT_TRUE(returns(rq_session_get_result(session.get(), &copy), RQ_OK));
    EXPECT_EQ(copy.end_reason, RQ_END_COMPLETED);
    EXPECT_EQ(copy.total_errors, result.total_errors);
    EXPECT_EQ(copy.verified_bytes, result.verified_bytes);
    EXPECT_EQ(copy.elapsed_ms, result.elapsed_ms);
    ASSERT_EQ(copy.step_result_count, k_step_count);
    ASSERT_NE(copy.step_results, nullptr);
    for (std::uint32_t i = 0; i < k_step_count; ++i) {
        EXPECT_EQ(std::memcmp(&copy.step_results[i], &seen.step_results[i], sizeof(rq_step_result)),
                  0)
            << "step " << i;
    }

    // Completion is final: a late stop is accepted and changes nothing, and start stays refused.
    EXPECT_TRUE(returns(rq_session_stop(session.get(), RQ_STOP_USER), RQ_OK));
    EXPECT_TRUE(returns(rq_session_start(session.get()), RQ_E_INVALID_STATE));
    copy = rq_run_result{};
    copy.struct_size = sizeof(rq_run_result);
    ASSERT_TRUE(returns(rq_session_get_result(session.get(), &copy), RQ_OK));
    EXPECT_EQ(copy.end_reason, RQ_END_COMPLETED);

    ASSERT_TRUE(returns(session.destroy(), RQ_OK));
    const std::uint64_t callbacks_at_destroy = recorder.callback_count();
    std::this_thread::sleep_for(k_quiet_period);
    EXPECT_EQ(recorder.callback_count(), callbacks_at_destroy) << "a callback ran after destroy";
}

TEST_F(ApiBoundaryRun, CreateDeepCopiesTheConfigurationStepsNamesAndCallbacks) {
    callback_recorder recorder;
    session_guard session;
    std::uint32_t worker_count = 0;
    {
        std::vector<rq_step_def> steps = valid_steps();
        rq_session_config config = valid_config(steps);
        rq_callbacks callbacks = recorder.callbacks();
        worker_count = config.worker_count;
        ASSERT_TRUE(returns(rq_session_create(&config, &callbacks, session.out()), RQ_OK));

        // Garbage wherever a shallow copy would still read, and the step storage freed.
        std::memset(steps.data(), 0xA5, steps.size() * sizeof(rq_step_def));
        steps.clear();
        steps.shrink_to_fit();
        std::memset(&config, 0xA5, sizeof(config));
        std::memset(&callbacks, 0, sizeof(callbacks));
    }
    recorder.set_handle(session.get());
    ASSERT_TRUE(returns(rq_session_start(session.get()), RQ_OK));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    EXPECT_EQ(recorder.trace(), k_complete_trace);
    const recording seen = recorder.snapshot();
    ASSERT_EQ(seen.step_starts.size(), k_step_count);
    EXPECT_EQ(fixed_text(seen.step_starts[0].name), k_step0_name);
    EXPECT_EQ(fixed_text(seen.step_starts[1].name), k_step1_name);
    EXPECT_EQ(seen.step_starts[1].count, k_step_count);
    EXPECT_EQ(seen.prepared.worker_count, worker_count);
    EXPECT_GE(seen.prepared.tested_bytes, k_min_region_bytes);
    EXPECT_LE(seen.prepared.tested_bytes, k_region_bytes);
    EXPECT_EQ(seen.completed.end_reason, RQ_END_COMPLETED);
    EXPECT_EQ(seen.completed.step_result_count, k_step_count);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

TEST_F(ApiBoundaryRun, DestroyFromInsideEveryCallbackIsRefusedAndTheRunContinues) {
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    callback_recorder recorder;
    recorder.probe_from_callbacks();
    session_guard session;
    ASSERT_TRUE(create_and_start(config, recorder, session));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    EXPECT_EQ(recorder.trace(), k_complete_trace);
    const recording seen = recorder.snapshot();
    EXPECT_FALSE(seen.callback_threw);
    EXPECT_EQ(seen.destroy_in_callback.size(), seen.events.size());
    for (std::size_t i = 0; i < seen.destroy_in_callback.size(); ++i) {
        EXPECT_TRUE(returns(seen.destroy_in_callback[i], RQ_E_INVALID_STATE))
            << "destroy inside structural callback " << i;
    }
    // The result is stored immediately before on_completed, never earlier.
    EXPECT_TRUE(returns(seen.get_result_in_prepared, RQ_E_INVALID_STATE));
    EXPECT_TRUE(returns(seen.get_result_in_completed, RQ_OK));
    EXPECT_TRUE(seen.get_result_matches_payload);
    EXPECT_EQ(seen.completed.end_reason, RQ_END_COMPLETED);

    // The refused destroys left the session registered; the host's destroy succeeds.
    EXPECT_EQ(live_session_count(), baseline_sessions_ + 1U);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

TEST_F(ApiBoundaryRun, DestroyWhileRunningReturnsAfterTheLastCallback) {
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    callback_recorder recorder;
    session_guard session;
    ASSERT_TRUE(create_and_start(config, recorder, session));
    ASSERT_TRUE(recorder.wait_until_step_started(0)) << "trace: " << recorder.trace();

    const rq_session_handle stale = session.get();
    ASSERT_TRUE(returns(session.destroy(), RQ_OK));
    const std::uint64_t callbacks_at_destroy = recorder.callback_count();
    std::this_thread::sleep_for(k_quiet_period);
    EXPECT_EQ(recorder.callback_count(), callbacks_at_destroy) << "a callback ran after destroy";
    EXPECT_FALSE(recorder.snapshot().callback_threw);
    EXPECT_TRUE(returns(rq_session_start(stale), RQ_E_INVALID_HANDLE));
}

/** An instruction set forced through isa_override. */
struct forced_isa_case {
    const char* name;
    std::uint32_t isa;
};

const forced_isa_case k_forced_isa_cases[] = {
    {"Sse2", RQ_ISA_SSE2},
    {"Avx2", RQ_ISA_AVX2},
    {"Avx512", RQ_ISA_AVX512},
};

class ApiBoundaryForcedIsa : public ApiBoundarySession,
                             public ::testing::WithParamInterface<forced_isa_case> {};

TEST_P(ApiBoundaryForcedIsa, IsTheSetThePreparedSessionReports) {
    if ((host_isa_mask() & (1U << GetParam().isa)) == 0U) {
        GTEST_SKIP() << "this CPU lacks " << GetParam().name;
    }
    const std::vector<rq_step_def> steps = valid_steps();
    rq_session_config config = valid_config(steps);
    config.isa_override = GetParam().isa;
    callback_recorder recorder;
    // A stop from inside on_prepared ends the run before the first gate.
    recorder.stop_in_prepared(RQ_STOP_USER);
    session_guard session;
    ASSERT_TRUE(create_and_start(config, recorder, session));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    EXPECT_EQ(recorder.trace(), "prepared completed released");
    const recording seen = recorder.snapshot();
    EXPECT_EQ(seen.prepared.isa_selected, GetParam().isa);
    EXPECT_TRUE(returns(seen.stop_in_prepared, RQ_OK));
    EXPECT_EQ(seen.completed.end_reason, RQ_END_USER_STOP);
    EXPECT_EQ(seen.completed.step_result_count, 0U);
    EXPECT_EQ(seen.released_bytes, seen.prepared.tested_bytes);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

INSTANTIATE_TEST_SUITE_P(ApiBoundaryEverySet, ApiBoundaryForcedIsa,
                         ::testing::ValuesIn(k_forced_isa_cases), case_name<forced_isa_case>);

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryStopBeforeStart, ApiBoundaryGateAbort and ApiBoundaryGateStop: end reasons
 * ------------------------------------------------------------------------------------------- */

/** Two stops before start with different reasons. */
struct stop_case {
    const char* name;
    std::uint32_t first;
    std::uint32_t second;
};

const stop_case k_stop_cases[] = {
    {"UserThenThermal", RQ_STOP_USER, RQ_STOP_THERMAL},
    {"ThermalThenHostFault", RQ_STOP_THERMAL, RQ_STOP_HOST_FAULT},
    {"HostFaultThenUser", RQ_STOP_HOST_FAULT, RQ_STOP_USER},
};

class ApiBoundaryStopBeforeStart : public ApiBoundarySession,
                                   public ::testing::WithParamInterface<stop_case> {};

TEST_P(ApiBoundaryStopBeforeStart, FirstReasonDecidesAndOnlyCompletedAndReleasedFollow) {
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    callback_recorder recorder;
    const rq_callbacks callbacks = recorder.callbacks();
    session_guard session;
    ASSERT_TRUE(returns(rq_session_create(&config, &callbacks, session.out()), RQ_OK));
    recorder.set_handle(session.get());

    // Idempotent: each call is accepted, and only the first reason is kept.
    EXPECT_TRUE(returns(rq_session_stop(session.get(), GetParam().first), RQ_OK));
    EXPECT_TRUE(returns(rq_session_stop(session.get(), GetParam().second), RQ_OK));
    EXPECT_TRUE(returns(rq_session_stop(session.get(), GetParam().first), RQ_OK));
    ASSERT_TRUE(returns(rq_session_start(session.get()), RQ_OK));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    EXPECT_EQ(recorder.trace(), "completed released") << "nothing is prepared after a stop";
    const recording seen = recorder.snapshot();
    const rq_run_result& result = seen.completed;
    EXPECT_EQ(result.end_reason, end_reason_for(GetParam().first));
    EXPECT_EQ(result.steps_completed, 0U);
    EXPECT_EQ(result.step_result_count, 0U);
    EXPECT_EQ(result.total_errors, 0U);
    EXPECT_EQ(result.verified_bytes, 0U);
    EXPECT_EQ(result.elapsed_ms, 0U) << "no test time exists without preparation";
    EXPECT_EQ(result.first_error_ms, RQ_NO_FIRST_ERROR);
    EXPECT_EQ(result.os_error, 0U);
    EXPECT_TRUE(is_terminated(result.end_detail));
    EXPECT_FALSE(fixed_text(result.end_detail).empty()) << "a stop carries a diagnostic";
    EXPECT_EQ(seen.released_bytes, 0U) << "nothing was allocated";

    rq_run_result copy{};
    copy.struct_size = sizeof(rq_run_result);
    ASSERT_TRUE(returns(rq_session_get_result(session.get(), &copy), RQ_OK));
    EXPECT_EQ(copy.end_reason, end_reason_for(GetParam().first));
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

INSTANTIATE_TEST_SUITE_P(ApiBoundaryEveryReasonFirst, ApiBoundaryStopBeforeStart,
                         ::testing::ValuesIn(k_stop_cases), case_name<stop_case>);

/** A step gate that refuses one step without recording a stop reason. */
struct gate_case {
    const char* name;
    std::uint32_t step;
    /** RQ_ACTION_ABORT, or another value the engine must treat as an abort. */
    std::uint32_t value;
};

const gate_case k_gate_cases[] = {
    {"AbortAtFirstStep", 0, RQ_ACTION_ABORT},
    {"AbortAtSecondStep", 1, RQ_ACTION_ABORT},
    {"UnknownActionAtFirstStep", 0, 7},
    {"UnknownActionAtSecondStep", 1, 7},
};

class ApiBoundaryGateAbort : public ApiBoundarySession,
                             public ::testing::WithParamInterface<gate_case> {};

TEST_P(ApiBoundaryGateAbort, WithoutARecordedReasonEndsAsHostFault) {
    const std::uint32_t aborted = GetParam().step;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    callback_recorder recorder;
    gate_plan plan{};
    plan.abort_step = aborted;
    plan.abort_value = GetParam().value;
    recorder.set_gate_plan(plan);
    session_guard session;
    ASSERT_TRUE(create_and_start(config, recorder, session));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    // The refused step starts no traffic: no on_step_finished, no step_results entry.
    EXPECT_EQ(recorder.trace(), aborted == 0U ? "prepared start0 completed released"
                                              : "prepared start0 end0 start1 completed released");
    const recording seen = recorder.snapshot();
    EXPECT_EQ(seen.step_results.size(), aborted);
    for (const rq_step_result& step : seen.step_results) {
        EXPECT_LT(step.index, aborted) << "the refused step has a result";
    }
    const rq_run_result& result = seen.completed;
    EXPECT_EQ(result.end_reason, RQ_END_HOST_FAULT);
    EXPECT_EQ(result.step_result_count, aborted);
    EXPECT_EQ(result.steps_completed, aborted);
    EXPECT_EQ(result.os_error, 0U);
    EXPECT_TRUE(is_terminated(result.end_detail));
    EXPECT_FALSE(fixed_text(result.end_detail).empty());
    EXPECT_EQ(seen.misplaced_progress, 0U) << "progress raised for the refused step";
    EXPECT_EQ(seen.released_bytes, seen.prepared.tested_bytes);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

INSTANTIATE_TEST_SUITE_P(ApiBoundaryEveryRefusal, ApiBoundaryGateAbort,
                         ::testing::ValuesIn(k_gate_cases), case_name<gate_case>);

class ApiBoundaryGateStop : public ApiBoundarySession {};

TEST_F(ApiBoundaryGateStop, ReasonRecordedInsideTheGateDecidesTheEndReason) {
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_session_config config = valid_config(steps);
    callback_recorder recorder;
    gate_plan plan{};
    plan.abort_step = 0;
    plan.abort_value = RQ_ACTION_ABORT;
    plan.stop_reason = RQ_STOP_THERMAL;
    recorder.set_gate_plan(plan);
    session_guard session;
    ASSERT_TRUE(create_and_start(config, recorder, session));
    ASSERT_TRUE(recorder.wait_until_released()) << "trace: " << recorder.trace();

    EXPECT_EQ(recorder.trace(), "prepared start0 completed released");
    const recording seen = recorder.snapshot();
    EXPECT_TRUE(returns(seen.stop_in_gate, RQ_OK));
    EXPECT_EQ(seen.completed.end_reason, RQ_END_THERMAL);
    EXPECT_EQ(seen.completed.step_result_count, 0U);
    EXPECT_TRUE(returns(session.destroy(), RQ_OK));
}

/* ---------------------------------------------------------------------------------------------
 * ApiBoundaryIsa: instruction-set availability as isa_override validation applies it
 * ------------------------------------------------------------------------------------------- */

/** A highest basic leaf a current CPU reports; any value from 7 up enables leaf 7. */
constexpr std::uint32_t k_modern_max_basic_leaf = 0x16U;

/** XCR0 with x87, SSE, AVX, opmask, ZMM_Hi256 and Hi16_ZMM state. */
constexpr std::uint64_t k_xcr0_through_hi16_zmm = 0xE7U;

/** XCR0 with x87, SSE and AVX state only: no opmask or ZMM state. */
constexpr std::uint64_t k_xcr0_through_avx = 0x7U;

/** Every AVX2 and AVX-512 requirement, built from the individual bit constants. */
constexpr cpu_features avx512_full_features() noexcept {
    cpu_features features{};
    features.max_basic_leaf = k_modern_max_basic_leaf;
    features.leaf1_ecx = k_cpuid1_ecx_osxsave | k_cpuid1_ecx_avx | k_cpuid1_ecx_fma;
    features.leaf7_ebx = k_cpuid7_ebx_avx2 | k_cpuid7_ebx_bmi1 | k_cpuid7_ebx_bmi2 |
                         k_cpuid7_ebx_avx512f | k_cpuid7_ebx_avx512dq | k_cpuid7_ebx_avx512cd |
                         k_cpuid7_ebx_avx512bw | k_cpuid7_ebx_avx512vl;
    features.xcr0 = k_xcr0_through_hi16_zmm;
    return features;
}

/** validate_config of the test plan forcing isa_override, against a synthetic ISA mask. */
rq_result validate_forcing(std::uint32_t isa_override, std::uint32_t available_mask) {
    callback_recorder recorder;  // Validated only; no session exists to raise its callbacks.
    const std::vector<rq_step_def> steps = valid_steps();
    rq_session_config config = valid_config(steps);
    config.isa_override = isa_override;
    const rq_callbacks callbacks = recorder.callbacks();
    validation_limits limits{};
    limits.logical_processors = host_logical_processors();
    limits.isa_available_mask = available_mask;
    return validate_config(&config, &callbacks, limits);
}

/** One AVX-512 requirement removed from the full feature set. */
struct missing_requirement_case {
    const char* name;
    std::uint32_t cleared_leaf7_bits;
    std::uint64_t xcr0;
};

const missing_requirement_case k_missing_requirement_cases[] = {
    {"WithoutF", k_cpuid7_ebx_avx512f, k_xcr0_through_hi16_zmm},
    {"WithoutCD", k_cpuid7_ebx_avx512cd, k_xcr0_through_hi16_zmm},
    {"WithoutBW", k_cpuid7_ebx_avx512bw, k_xcr0_through_hi16_zmm},
    {"WithoutDQ", k_cpuid7_ebx_avx512dq, k_xcr0_through_hi16_zmm},
    {"WithoutVL", k_cpuid7_ebx_avx512vl, k_xcr0_through_hi16_zmm},
    {"WithoutZmmState", 0U, k_xcr0_through_avx},
};

class ApiBoundaryIsa : public ::testing::TestWithParam<missing_requirement_case> {};

TEST_P(ApiBoundaryIsa, MissingRequirementMakesAvx512UnavailableAndForcingItInvalid) {
    cpu_features features = avx512_full_features();
    features.leaf7_ebx &= ~GetParam().cleared_leaf7_bits;
    features.xcr0 = GetParam().xcr0;

    const isa_selection automatic = select_isa(features, RQ_ISA_AUTO);
    EXPECT_NE(automatic.best, RQ_ISA_AVX512);
    EXPECT_EQ(automatic.best, RQ_ISA_AVX2) << "the AVX2 condition is still complete";
    EXPECT_EQ(automatic.available_mask & (1U << RQ_ISA_AVX512), 0U);
    const isa_selection forced = select_isa(features, RQ_ISA_AVX512);
    EXPECT_TRUE(forced.override_rejected);
    EXPECT_NE(forced.selected, RQ_ISA_AVX512);

    // Session creation checks isa_override against exactly this mask.
    EXPECT_TRUE(
        returns(validate_forcing(RQ_ISA_AVX512, automatic.available_mask), RQ_E_INVALID_ARGUMENT));
    EXPECT_TRUE(returns(validate_forcing(RQ_ISA_AVX2, automatic.available_mask), RQ_OK));
    EXPECT_TRUE(returns(validate_forcing(RQ_ISA_AUTO, automatic.available_mask), RQ_OK));
}

INSTANTIATE_TEST_SUITE_P(ApiBoundaryEachRequirement, ApiBoundaryIsa,
                         ::testing::ValuesIn(k_missing_requirement_cases),
                         case_name<missing_requirement_case>);

TEST(ApiBoundaryIsaComplete, FullAvx512SetIsSelectedAndMayBeForced) {
    const isa_selection automatic = select_isa(avx512_full_features(), RQ_ISA_AUTO);
    EXPECT_EQ(automatic.best, RQ_ISA_AVX512);
    EXPECT_EQ(automatic.selected, RQ_ISA_AVX512);
    EXPECT_EQ(automatic.available_mask,
              (1U << RQ_ISA_SSE2) | (1U << RQ_ISA_AVX2) | (1U << RQ_ISA_AVX512));
    EXPECT_TRUE(returns(validate_forcing(RQ_ISA_AVX512, automatic.available_mask), RQ_OK));
}

TEST(ApiBoundaryIsaHost, CreateRejectsForcingASetThisCpuLacks) {
    const std::uint32_t mask = host_isa_mask();
    std::vector<std::uint32_t> missing;
    for (const std::uint32_t isa : {RQ_ISA_SSE2, RQ_ISA_AVX2, RQ_ISA_AVX512}) {
        if ((mask & (1U << isa)) == 0U) {
            missing.push_back(isa);
        }
    }
    if (missing.empty()) {
        GTEST_SKIP() << "this CPU has every set; ApiBoundaryIsa covers the rule synthetically";
    }

    callback_recorder recorder;
    const std::vector<rq_step_def> steps = valid_steps();
    const rq_callbacks callbacks = recorder.callbacks();
    const std::uint32_t before = live_session_count();
    for (const std::uint32_t isa : missing) {
        rq_session_config config = valid_config(steps);
        config.isa_override = isa;
        rq_session_handle handle = nullptr;
        EXPECT_TRUE(returns(rq_session_create(&config, &callbacks, &handle), RQ_E_INVALID_ARGUMENT))
            << "isa_override " << isa;
        EXPECT_EQ(handle, nullptr);
        if (handle != nullptr) {
            static_cast<void>(rq_session_destroy(handle));
        }
    }
    EXPECT_EQ(live_session_count(), before);
}

}  // namespace
}  // namespace rq::engine::test
