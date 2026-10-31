/**
 * @file test_stop.cpp
 * @brief Stop latency: on_completed within 2 s of a user stop in every phase kind, and of the
 *        first confirmed error with Stop on error on, on a locked region of at least 1 GiB,
 *        with the release time after it measured and logged.
 *
 * StopAtRandomPoints/stop_latency runs one production session per case through core/session.h
 * (qpc_clock, the real memory-access layer under counting_memory_access, win32 thread affinity)
 * with one worker per logical processor and a single 60 s step of the phase kind under test,
 * so the stop always lands inside that step. Each phase kind runs at three random points:
 * - preparing: rq_session_stop 0-300 ms after rq_session_start, before on_prepared, so the
 *   stop meets memory preparation at chunk granularity or the worker pool start-up;
 * - windowed write and windowed verify: RANDOM, SEQUENTIAL_ASCENDING;
 * - hold: RANDOM, RANDOM_BLOCKS with a 30 s hold, stopped 0-5 s into the hold;
 * - moving inversion: MOVING_INVERSION_WALKING sweeps;
 * - turnaround: RANDOM, TURNAROUND.
 *
 * stop_latency_preparation_release stops the same 1 GiB run inside preparation with the whole
 * region locked, deterministically, and makes releasing it take longer than the bound. Its affinity
 * seam holds the first worker's pin, which the pool waits for only once allocation has locked every
 * chunk, until the stop has been requested; its free seam frees each chunk with VirtualFree after
 * a fixed delay, so the release takes at least 3 s. Completed must still arrive within 2 s of the
 * stop, every chunk must be freed after Completed and before Released, and Released must report
 * the whole region: the chunks a stop during preparation leaves locked are released only after
 * Completed, so a release that grows with the region never delays it. Its release_ms must reach
 * the delay the seam forces, which shows the release outlasted the bound; like every release
 * time here, it is never bounded.
 *
 * A counting_memory_access observer detects the phases. It runs on a worker thread, inside the
 * counting layer's primitive call, and sets an auto-reset event; the test thread waits on that
 * event, so every stop is requested, and its QueryPerformanceCounter instant taken, on the test
 * thread. For traffic phases the test first waits for the phase, sleeps a random 0-200 ms, then
 * arms a handshake: the worker issuing the next primitive of that phase signals and then waits
 * inside that call, unable to leave the phase, until the test thread has requested the stop and
 * released it. Its pause and resume instants bracket the stop instant, which proves the stop
 * landed inside the phase rather than wherever the sleep or the test thread's wake-up ended.
 * The wait is bounded by the 30 s phase timeout, and the session's owner releases it on every
 * exit path before joining the workers, so a failed assertion never leaves a worker held. The
 * 2 s bound is still measured once, from the stop request to on_completed. The hold is detected
 * when every worker has issued both fences of its pre-hold flush, just before the coordinator
 * enters the hold.
 *
 * The gate is the 2 s bound from the stop request to on_completed, which ramquick_engine.h
 * places on the engine. Release time grows with the region size and is reported, not bounded,
 * so release_ms is recorded as an XML property and printed but never gated. released_bytes is
 * recorded too, and checked only as an amount: the tested bytes once Prepared was raised, and
 * the chunks locked before the stop, at most the region, after a stop during preparation.
 * TearDownTestSuite records the largest stop latency of the run.
 *
 * StopOnFirstError/first_error_latency gates the other bound ramquick_engine.h places on the
 * engine: with Stop on error on, on_completed within 2 s of the first confirmed error. Each run
 * is a production session over fault_injecting_memory_access -> counting_memory_access -> the
 * real layer, the fault layer and the session sharing one qpc_clock, with the region and worker
 * count of stop_latency and a single 60 s step. No host stop is ever requested, so only the
 * confirming worker's own RQ_END_FIRST_ERROR request can end the step early. The fault layer
 * flips bit 17 in a few seeded words of every pass, which the first pass's verification meets in
 * the phase kind under test, three runs each: windowed verify, verification after a 1 s hold,
 * moving-inversion sweeps and turnaround. The error collector re-reads every mismatch once
 * through the outermost layer and only then requests the stop, so a counting-layer observer
 * stamps the QPC instant of the earliest re-read, with atomics only, on the worker. That instant
 * precedes the stop request by no more than the error's classification and recording, so the
 * measured interval is never shorter than the true one. Each run also checks the end reason and
 * detail, that every error is a logged injection re-read once in the phase kind under test, and
 * the release after Completed, whose time is reported, not bounded. Its engine and fault seeds
 * come from its own suite seed, which RAMQUICK_STOP_TEST_SEED replays as well.
 *
 * Random points come from a std::mt19937_64 per case, seeded from a suite seed recorded as an
 * XML property; setting RAMQUICK_STOP_TEST_SEED to that value replays the same delays and engine
 * seeds.
 *
 * Nothing here needs elevation: the engine raises the minimum working set before locking, which
 * the default policy permits. The suite fails, rather than skips, when less than 2 GiB of
 * physical memory is available, because the latency it gates is defined on a 1 GiB region.
 *
 * CloseHandleChecked and close_handle_checked_failure cover close_handle_checked, through which the
 * stop signal and the engine's other handle owners close their handles: a failed close is
 * reported once, to the caller and to the debugger, and never retried. They need neither memory
 * nor elevation, so the 2 GiB precondition does not apply to them. Their events are held by
 * test_event_owner, which lifts close protection and closes on every exit, so a failing case
 * leaves no protected handle open in the process the rest of the suite runs in.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <ostream>
#include <random>
#include <stdexcept>
#include <string>
#include <system_error>
#include <tuple>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/memory/memory_access.h"
#include "core/memory/memory_region.h"
#include "core/session.h"
#include "core/runner/worker_pool.h"
#include "fakes/counting_memory_access.h"
#include "fakes/fault_spec.h"
#include "fakes/fault_injecting_memory_access.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

constexpr std::uint64_t k_mib = std::uint64_t{1} << 20U;
constexpr std::uint64_t k_gib = std::uint64_t{1} << 30U;

/** The region under test is the smallest one of whole 2 MiB slices that reaches 1 GiB. */
constexpr std::uint64_t k_target_region_bytes = k_gib;

/** Below this much available physical memory the suite fails its precondition. */
constexpr std::uint64_t k_min_available_bytes = 2 * k_gib;

/** Workers are capped so that every slice of the 1 GiB region holds at least 4 MiB. */
constexpr std::uint64_t k_min_worker_slice_bytes = 4 * k_mib;
constexpr std::uint32_t k_max_workers =
    static_cast<std::uint32_t>(k_target_region_bytes / k_min_worker_slice_bytes);

/** The single step's budget: far beyond every stop point, so the stop lands inside it. */
constexpr std::uint32_t k_step_budget_ms = 60'000U;
/** Hold of the hold case: the stop lands 0-5 s into it. */
constexpr std::uint32_t k_hold_ms = 30'000U;
/** 25 %, any value > 0: coverage budgets never extend a step. */
constexpr std::uint32_t k_coverage_budget_bp = 2'500U;
/** As the application passes them. */
constexpr std::uint32_t k_error_detail_cap = 10'000U;
constexpr std::uint32_t k_progress_interval_ms = 250U;

/** The bound under test: stop request to on_completed. */
constexpr std::uint64_t k_ns_per_ms = 1'000'000U;
constexpr std::uint64_t k_stop_bound_ns = 2'000U * k_ns_per_ms;

/** Waits that only keep a broken engine from hanging the suite; none of them is a gate. */
constexpr DWORD k_phase_timeout_ms = 30'000U;
constexpr std::chrono::milliseconds k_completed_timeout{10'000};
constexpr std::chrono::milliseconds k_released_timeout{120'000};

/** Random stop points per phase kind; an int because ::testing::Range generates the points. */
constexpr int k_points_per_kind = 3;
constexpr std::uint64_t k_points_per_kind_u64 = static_cast<std::uint64_t>(k_points_per_kind);

/**
 * Attempts of the preparing case. Each attempt that finds on_prepared already raised aims the
 * next one at under half its delay and its measured preparation time.
 */
constexpr std::uint32_t k_prepare_attempts = 8U;

/**
 * Least time the preparation-release case's free seam makes releasing the region take, spread
 * evenly over its chunks. Longer than the bound, so a Completed that waited for the release, as
 * it would if the chunks locked before a stop were freed first, could not meet it on any machine.
 */
constexpr std::uint64_t k_slow_release_ms = 3'000U;
static_assert(k_slow_release_ms * k_ns_per_ms > k_stop_bound_ns,
              "the forced release must outlast the stop bound");

/** Engine seed of the preparation-release case; it runs no traffic, so any value serves. */
constexpr std::uint64_t k_preparation_release_seed = 0x51A4CE5E0F8B1D27ULL;

/** Environment variable that replays a recorded suite seed. */
constexpr char k_seed_variable[] = "RAMQUICK_STOP_TEST_SEED";

/* ---------------------------------------------------------------------------------------------
 * Cases
 * ------------------------------------------------------------------------------------------- */

/** Where the stop lands. */
enum class stop_case : std::uint32_t {
    preparing,
    windowed_write,
    windowed_verify,
    hold,
    moving_inversion,
    turnaround,
};

/** One phase kind: its step, the phase the observer waits for and the random-delay range. */
struct case_spec {
    stop_case kind;
    /** Test-name fragment and XML property suffix. */
    const char* label;
    const char* step_name;
    std::uint32_t pattern;
    /** Moving inversion ignores the order, but validation still requires a valid one. */
    std::uint32_t access_order;
    std::uint32_t hold_ms;
    /** Phase that triggers the stop; unused by the preparing case. */
    phase_kind target;
    /** Upper bound of the random delay: after start (preparing) or after the phase began. */
    std::uint32_t max_delay_ms;
};

constexpr std::array<case_spec, 6> k_cases{{
    {stop_case::preparing, "preparing", "Stop while preparing", RQ_PATTERN_RANDOM,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::write, 300U},
    {stop_case::windowed_write, "windowed_write", "Stop in windowed write", RQ_PATTERN_RANDOM,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::write, 200U},
    {stop_case::windowed_verify, "windowed_verify", "Stop in windowed verify", RQ_PATTERN_RANDOM,
     RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::verify, 200U},
    {stop_case::hold, "hold", "Stop in refresh hold", RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS,
     k_hold_ms, phase_kind::pre_hold_flush, 5'000U},
    {stop_case::moving_inversion, "moving_inversion", "Stop in moving inversion",
     RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, phase_kind::sweep,
     200U},
    {stop_case::turnaround, "turnaround", "Stop in turnaround", RQ_PATTERN_RANDOM,
     RQ_ORDER_TURNAROUND, 0U, phase_kind::turnaround, 200U},
}};

constexpr bool cases_in_enum_order() noexcept {
    for (std::size_t i = 0; i < k_cases.size(); ++i) {
        if (static_cast<std::size_t>(k_cases[i].kind) != i) {
            return false;
        }
    }
    return true;
}
static_assert(cases_in_enum_order(), "k_cases is indexed by stop_case");

const case_spec& spec_for(stop_case kind) noexcept {
    return k_cases[static_cast<std::size_t>(kind)];
}

/** gtest prints parameters through this, so failures name the case instead of raw bytes. */
void PrintTo(stop_case kind, std::ostream* os) {
    *os << spec_for(kind).label;
}

const char* phase_name(phase_kind phase) noexcept {
    switch (phase) {
    case phase_kind::write:
        return "write";
    case phase_kind::pre_hold_flush:
        return "pre_hold_flush";
    case phase_kind::verify:
        return "verify";
    case phase_kind::sweep:
        return "sweep";
    case phase_kind::turnaround:
        return "turnaround";
    case phase_kind::reread:
        return "reread";
    }
    return "invalid phase";
}

/* ---------------------------------------------------------------------------------------------
 * Time, seeds and formatting
 * ------------------------------------------------------------------------------------------- */

/** QueryPerformanceCounter in nanoseconds; one clock for the test thread and every callback. */
std::uint64_t qpc_now_ns() noexcept {
    static const qpc_clock clock;
    return clock.now_ns();
}

/** Nanoseconds from then to now; 0 if now is not later. */
constexpr std::uint64_t since(std::uint64_t now, std::uint64_t then) noexcept {
    return now > then ? now - then : 0;
}

double to_ms(std::uint64_t ns) noexcept {
    return static_cast<double>(ns) / static_cast<double>(k_ns_per_ms);
}

std::string format_ms(std::uint64_t ns) {
    char text[32];
    std::snprintf(text, sizeof(text), "%.3f", to_ms(ns));
    return text;
}

/** 0x followed by 16 uppercase hex digits. */
std::string hex64(std::uint64_t value) {
    char text[19];
    std::snprintf(text, sizeof(text), "0x%016llX", static_cast<unsigned long long>(value));
    return text;
}

/** SplitMix64 finaliser: derives an independent per-case seed from the suite seed. */
constexpr std::uint64_t mix_seed(std::uint64_t seed, std::uint64_t salt) noexcept {
    std::uint64_t z = seed + 0x9E3779B97F4A7C15ULL * (salt + 1U);
    z = (z ^ (z >> 30U)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27U)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31U);
}

std::uint32_t random_delay_ms(std::mt19937_64& rng, std::uint32_t max_ms) {
    return std::uniform_int_distribution<std::uint32_t>(0U, max_ms)(rng);
}

/**
 * A suite seed: the value of RAMQUICK_STOP_TEST_SEED, so a recorded run replays, or a fresh one
 * from std::random_device. A value that is not an unsigned integer fails the suite rather than
 * being silently replaced, since the run would not replay what its reader asked for.
 */
std::uint64_t resolve_suite_seed() {
    char text[32] = {};
    const DWORD length = GetEnvironmentVariableA(k_seed_variable, text, sizeof(text));
    if (length > 0U && length < sizeof(text)) {
        char* end = nullptr;
        const unsigned long long parsed = std::strtoull(text, &end, 0);
        if (end != text && *end == '\0') {
            return static_cast<std::uint64_t>(parsed);
        }
        ADD_FAILURE() << k_seed_variable << "='" << text
                      << "' is not an unsigned integer; set it to a recorded suite_seed";
    }
    std::random_device device;
    return (std::uint64_t{device()} << 32U) ^ device();
}

/**
 * Bytes of the region under test for the given worker count: whole 2 MiB slices reaching at
 * least k_target_region_bytes, 1 GiB.
 *
 * The engine rounds each slice down to k_min_slice_bytes and ends a run whose rounded slices
 * test fewer than min_region_bytes with RQ_END_INSUFFICIENT_MEMORY before locking anything. A
 * literal 1 GiB region would therefore fail on every logical-processor count that does not
 * divide 512; rounding the slice up keeps one worker per logical processor and at least 1 GiB
 * locked, and equals 1 GiB exactly on power-of-two counts.
 */
constexpr std::uint64_t region_bytes_for(std::uint32_t workers) noexcept {
    const std::uint64_t per_worker = (k_target_region_bytes + workers - 1U) / workers;
    const std::uint64_t slice =
        (per_worker + k_min_slice_bytes - 1U) / k_min_slice_bytes * k_min_slice_bytes;
    return slice * workers;
}
static_assert(region_bytes_for(128U) == k_gib, "power-of-two worker counts test exactly 1 GiB");
static_assert(region_bytes_for(12U) >= k_gib &&
                  region_bytes_for(12U) % (12U * k_min_slice_bytes) == 0,
              "other counts round each slice up to whole 2 MiB");

std::string end_reason_name(std::uint32_t reason) {
    switch (reason) {
    case RQ_END_COMPLETED:
        return "COMPLETED";
    case RQ_END_FIRST_ERROR:
        return "FIRST_ERROR";
    case RQ_END_USER_STOP:
        return "USER_STOP";
    case RQ_END_THERMAL:
        return "THERMAL";
    case RQ_END_HOST_FAULT:
        return "HOST_FAULT";
    case RQ_END_INSUFFICIENT_MEMORY:
        return "INSUFFICIENT_MEMORY";
    case RQ_END_AFFINITY_FAILED:
        return "AFFINITY_FAILED";
    case RQ_END_INTERNAL_FAULT:
        return "INTERNAL_FAULT";
    default:
        return "end reason " + std::to_string(reason);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Callback recorder
 * ------------------------------------------------------------------------------------------- */

/**
 * What one session's callbacks delivered, with the QPC instant of each lifecycle event: a plain
 * copy that run_recorder::snapshot() takes, so its counters and flag need no atomics.
 */
struct run_record {
    std::uint32_t gates = 0;
    std::uint32_t prepared_calls = 0;
    std::uint64_t prepared_ns = 0;
    rq_prepared prepared_info{};

    std::uint32_t completed_calls = 0;
    std::uint64_t completed_ns = 0;
    /** The on_completed payload; step_results is cleared, its pointer was valid for the call. */
    rq_run_result result{};
    /** The single step's result, when step_result_count is 1. */
    bool has_step_result = false;
    rq_step_result step_result{};

    std::uint32_t released_calls = 0;
    std::uint64_t released_ns = 0;
    std::uint64_t released_bytes = 0;
    std::uint64_t release_ms = 0;

    bool prepared() const noexcept { return prepared_calls != 0; }
    bool completed() const noexcept { return completed_calls != 0; }
    bool released() const noexcept { return released_calls != 0; }

    std::string end_detail() const {
        const char* const begin = result.end_detail;
        return std::string(begin, std::find(begin, begin + RQ_END_DETAIL_SIZE, '\0'));
    }

    /** One line for failure messages. */
    std::string describe() const {
        std::string text = "prepared=" + std::to_string(prepared_calls) +
                           " gates=" + std::to_string(gates) +
                           " completed=" + std::to_string(completed_calls) +
                           " released=" + std::to_string(released_calls);
        if (completed()) {
            text += " end_reason=" + end_reason_name(result.end_reason) +
                    " os_error=" + std::to_string(result.os_error) + " end_detail='" +
                    end_detail() + "'";
        }
        return text;
    }
};

/**
 * The session's callback set. Every callback runs on the coordinator; each one only stamps the
 * time, copies its payload under the lock and notifies, so none of them delays the engine.
 *
 * The counters and the step-result flag the coordinator shares with the test thread are atomics.
 * They are still changed and read under the lock, which orders them with the payload and with
 * the waits' predicates, so relaxed ordering suffices and no notification can be missed.
 */
class run_recorder {
public:
    run_recorder() = default;
    run_recorder(const run_recorder&) = delete;
    run_recorder& operator=(const run_recorder&) = delete;

    /** All seven callbacks, which rq_session_create requires, with this as user_data. */
    rq_callbacks callbacks() noexcept {
        rq_callbacks set{};
        set.struct_size = sizeof(rq_callbacks);
        set.reserved0 = 0;
        set.user_data = this;
        set.on_prepared = &run_recorder::on_prepared;
        set.on_step_starting = &run_recorder::on_step_starting;
        set.on_step_finished = &run_recorder::on_step_finished;
        set.on_progress = &run_recorder::on_progress;
        set.on_errors = &run_recorder::on_errors;
        set.on_completed = &run_recorder::on_completed;
        set.on_released = &run_recorder::on_released;
        return set;
    }

    bool wait_completed(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [this] {
            return completed_calls_.load(std::memory_order_relaxed) != 0U;
        });
    }

    bool wait_released(std::chrono::milliseconds timeout) {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [this] {
            return released_calls_.load(std::memory_order_relaxed) != 0U;
        });
    }

    /** The payload and the counters, copied together under the lock. */
    run_record snapshot() const {
        std::lock_guard<std::mutex> lock(mutex_);
        run_record copy = record_;
        copy.gates = gates_.load(std::memory_order_relaxed);
        copy.prepared_calls = prepared_calls_.load(std::memory_order_relaxed);
        copy.completed_calls = completed_calls_.load(std::memory_order_relaxed);
        copy.has_step_result = has_step_result_.load(std::memory_order_relaxed);
        copy.released_calls = released_calls_.load(std::memory_order_relaxed);
        return copy;
    }

private:
    static run_recorder& self(void* user_data) noexcept {
        return *static_cast<run_recorder*>(user_data);
    }

    static void RQ_CALL on_prepared(void* user_data, const rq_prepared* info) noexcept {
        const std::uint64_t now = qpc_now_ns();
        run_recorder& recorder = self(user_data);
        {
            std::lock_guard<std::mutex> lock(recorder.mutex_);
            recorder.prepared_calls_.fetch_add(1U, std::memory_order_relaxed);
            recorder.record_.prepared_ns = now;
            recorder.record_.prepared_info = *info;
        }
        recorder.changed_.notify_all();
    }

    static std::uint32_t RQ_CALL on_step_starting(void* user_data, const rq_step_start*) noexcept {
        run_recorder& recorder = self(user_data);
        std::lock_guard<std::mutex> lock(recorder.mutex_);
        recorder.gates_.fetch_add(1U, std::memory_order_relaxed);
        return RQ_ACTION_CONTINUE;
    }

    static void RQ_CALL on_step_finished(void*, const rq_step_result*) noexcept {}

    static void RQ_CALL on_progress(void*, const rq_progress*) noexcept {}

    static void RQ_CALL on_errors(void*, const rq_error_record*, std::uint32_t,
                                  std::uint64_t) noexcept {}

    static void RQ_CALL on_completed(void* user_data, const rq_run_result* result) noexcept {
        // Stamped first: the bound under test ends at this callback's entry.
        const std::uint64_t now = qpc_now_ns();
        run_recorder& recorder = self(user_data);
        {
            std::lock_guard<std::mutex> lock(recorder.mutex_);
            run_record& record = recorder.record_;
            recorder.completed_calls_.fetch_add(1U, std::memory_order_relaxed);
            record.completed_ns = now;
            record.result = *result;
            record.result.step_results = nullptr;
            const bool has_step_result = result->step_result_count == 1U;
            recorder.has_step_result_.store(has_step_result, std::memory_order_relaxed);
            if (has_step_result) {
                record.step_result = result->step_results[0];
            }
        }
        recorder.changed_.notify_all();
    }

    static void RQ_CALL on_released(void* user_data, std::uint64_t released_bytes,
                                    std::uint64_t release_ms) noexcept {
        const std::uint64_t now = qpc_now_ns();
        run_recorder& recorder = self(user_data);
        {
            std::lock_guard<std::mutex> lock(recorder.mutex_);
            recorder.released_calls_.fetch_add(1U, std::memory_order_relaxed);
            recorder.record_.released_ns = now;
            recorder.record_.released_bytes = released_bytes;
            recorder.record_.release_ms = release_ms;
        }
        recorder.changed_.notify_all();
    }

    mutable std::mutex mutex_;
    std::condition_variable changed_;
    // The payload only: its counters and has_step_result stay at their defaults here, and
    // snapshot() fills them in from the atomics below.
    run_record record_;
    std::atomic<std::uint32_t> gates_{0};
    std::atomic<std::uint32_t> prepared_calls_{0};
    std::atomic<std::uint32_t> completed_calls_{0};
    std::atomic<bool> has_step_result_{false};
    std::atomic<std::uint32_t> released_calls_{0};
};

/* ---------------------------------------------------------------------------------------------
 * Phase trigger
 * ------------------------------------------------------------------------------------------- */

/** How a Win32 event resets once a wait has seen it signalled. */
enum class event_reset : std::uint32_t {
    /** One satisfied wait resets it: each SetEvent wakes exactly one wait. */
    automatic,
    /** It stays signalled until reset, so every later wait returns at once. */
    manual,
};

/** Owns a Win32 event handle. */
class event_handle {
public:
    /** An unsignalled event. @throws std::system_error when it cannot be created. */
    explicit event_handle(event_reset reset = event_reset::automatic)
        : handle_(
              CreateEventW(nullptr, reset == event_reset::manual ? TRUE : FALSE, FALSE, nullptr)) {
        if (handle_ == nullptr) {
            throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                    "CreateEventW failed");
        }
    }
    ~event_handle() { CloseHandle(handle_); }
    event_handle(const event_handle&) = delete;
    event_handle& operator=(const event_handle&) = delete;

    HANDLE get() const noexcept { return handle_; }

private:
    HANDLE handle_;
};

/** What the handshake of phase_trigger::arm_handshake() saw of the one worker it held. */
struct worker_pause {
    /** A worker was held: the handshake met a primitive of the target phase. */
    bool paused = false;
    /** Its wait ended on release(), not on the k_phase_timeout_ms bound. */
    bool released_by_test = false;
    /** QPC instant the held worker signalled the test thread, after its primitive's access. */
    std::uint64_t begin_ns = 0;
    /** QPC instant its wait returned, before it went back to the runner. */
    std::uint64_t resume_ns = 0;
};

/**
 * The counting layer's observer: signals the test thread when the target phase is reached.
 *
 * observe() runs on worker threads, inside the counting layer's primitive call: after the access
 * has been forwarded and before the call returns to the runner. The layer calls it under
 * noexcept with no lock held, so it never calls a session function and never throws.
 * - Traffic phases: while armed, the first primitive of the target phase disarms the trigger
 *   and sets the event. arm() rearms it for the next primitive of that phase.
 *   arm_handshake() rearms it so that the worker issuing that primitive also waits, inside the
 *   call, until release(). A held worker cannot leave the phase, so a stop the test thread
 *   requests while it waits lands inside that phase however late the test thread is scheduled.
 *   The wait is bounded by k_phase_timeout_ms, so a test that never releases cannot hang the
 *   run, and worker_pause records both instants and whether release() ended it, which is how a
 *   test proves its stop fell inside the pause. Only the worker that disarms the trigger waits,
 *   and the resume event stays signalled once released, so nothing waits after release().
 * - Hold (hold_entry_fences != 0): every worker's pre-hold flush is a fence, its flushes and a
 *   fence, so the event is set when pass 0 has issued hold_entry_fences pre_hold_flush fences,
 *   two per worker: the moment the coordinator crosses the barrier into the hold. Verify-phase
 *   primitives are counted too, because a stop inside the hold must leave none. No worker is
 *   held: holding the one that issues the last pre-hold fence would keep the coordinator out of
 *   the hold the stop must meet.
 */
class phase_trigger {
public:
    phase_trigger(phase_kind target, std::uint32_t hold_entry_fences)
        : target_(target), hold_entry_fences_(hold_entry_fences) {}
    phase_trigger(const phase_trigger&) = delete;
    phase_trigger& operator=(const phase_trigger&) = delete;

    void observe(const primitive_record& record) noexcept {
        const phase_kind phase = record.ctx.phase;
        if (hold_entry_fences_ != 0U) {
            if (phase == phase_kind::pre_hold_flush && record.kind == primitive_kind::fence &&
                record.ctx.pass_index == 0U &&
                hold_fences_.fetch_add(1U, std::memory_order_acq_rel) + 1U == hold_entry_fences_) {
                fire();
            } else if (phase == phase_kind::verify) {
                verify_primitives_.fetch_add(1U, std::memory_order_relaxed);
            }
            return;
        }
        // The plain load keeps the shared flag read-mostly while disarmed. The exchange lets
        // exactly one worker disarm, so at most one is held per arming.
        if (phase == target_ && armed_.load(std::memory_order_relaxed) &&
            armed_.exchange(false, std::memory_order_acq_rel)) {
            if (handshake_.exchange(false, std::memory_order_acq_rel)) {
                pause_worker();
            } else {
                fire();
            }
        }
    }

    /** Rearms a traffic-phase trigger after its event has been consumed. */
    void arm() noexcept { armed_.store(true, std::memory_order_release); }

    /**
     * Rearms a traffic-phase trigger so that the worker issuing the next target-phase primitive
     * is held inside that call until release(). A no-op once release() has run.
     */
    void arm_handshake() noexcept {
        if (released_.load(std::memory_order_acquire)) {
            return;
        }
        // Published by arm()'s release store, which the disarming exchange acquires.
        handshake_.store(true, std::memory_order_relaxed);
        arm();
    }

    /**
     * Lets the held worker go, and any later one at once: the manual-reset resume event is never
     * reset. Every exit path calls it before the session joins its workers.
     */
    void release() noexcept {
        released_.store(true, std::memory_order_release);
        SetEvent(resume_.get());
    }

    /** Waits for the event; true when it was set within the timeout. */
    bool wait(DWORD timeout_ms) const noexcept {
        return WaitForSingleObject(event_.get(), timeout_ms) == WAIT_OBJECT_0;
    }

    /** QPC instant of the last firing; 0 before the first. */
    std::uint64_t fired_ns() const noexcept { return fired_ns_.load(std::memory_order_acquire); }

    /** The handshake's pause; complete once the held worker has returned to the runner. */
    worker_pause pause() const noexcept {
        worker_pause result;
        result.paused = paused_.load(std::memory_order_acquire);
        result.released_by_test = released_by_test_.load(std::memory_order_acquire);
        result.begin_ns = pause_begin_ns_.load(std::memory_order_acquire);
        result.resume_ns = resume_ns_.load(std::memory_order_acquire);
        return result;
    }

    /** Hold mode only: verify-phase primitives seen so far. */
    std::uint64_t verify_primitives() const noexcept {
        return verify_primitives_.load(std::memory_order_relaxed);
    }

private:
    void fire() noexcept {
        fired_ns_.store(qpc_now_ns(), std::memory_order_release);
        SetEvent(event_.get());
    }

    void pause_worker() noexcept {
        // Stamped before the event, so the test thread's stop instant cannot precede it.
        const std::uint64_t begin_ns = qpc_now_ns();
        pause_begin_ns_.store(begin_ns, std::memory_order_release);
        fired_ns_.store(begin_ns, std::memory_order_release);
        paused_.store(true, std::memory_order_release);
        SetEvent(event_.get());
        const DWORD outcome = WaitForSingleObject(resume_.get(), k_phase_timeout_ms);
        // Stamped after the wait, so a release issued after the stop orders it after the stop.
        resume_ns_.store(qpc_now_ns(), std::memory_order_release);
        released_by_test_.store(outcome == WAIT_OBJECT_0, std::memory_order_release);
    }

    const phase_kind target_;
    const std::uint32_t hold_entry_fences_;
    event_handle event_;
    event_handle resume_{event_reset::manual};
    std::atomic<bool> armed_{true};
    std::atomic<bool> handshake_{false};
    std::atomic<bool> released_{false};
    std::atomic<std::uint32_t> hold_fences_{0};
    std::atomic<std::uint64_t> verify_primitives_{0};
    std::atomic<std::uint64_t> fired_ns_{0};
    std::atomic<bool> paused_{false};
    std::atomic<bool> released_by_test_{false};
    std::atomic<std::uint64_t> pause_begin_ns_{0};
    std::atomic<std::uint64_t> resume_ns_{0};
};

/* ---------------------------------------------------------------------------------------------
 * Session under test
 * ------------------------------------------------------------------------------------------- */

/**
 * One production session over a counting layer: its single-step plan, its recorder and its
 * phase trigger. The destructor releases the trigger's handshake, then destroys the session,
 * which joins the coordinator and guarantees no callback or observer call runs after it, so the
 * recorder and the trigger outlive every engine reference to them on every exit path.
 */
class stop_session {
public:
    stop_session(const case_spec& spec, std::uint32_t workers, std::uint64_t engine_seed)
        : stop_session(spec, workers, engine_seed, region_bytes_for(workers)) {}

    /** As above over region_bytes, which is also the minimum, so no retry shrinks it. */
    stop_session(const case_spec& spec, std::uint32_t workers, std::uint64_t engine_seed,
                 std::uint64_t region_bytes)
        : trigger_(spec.target, spec.target == phase_kind::pre_hold_flush ? 2U * workers : 0U) {
        copy_utf8_truncated(step_.name, sizeof(step_.name), spec.step_name);
        step_.pattern = spec.pattern;
        step_.access_order = spec.access_order;
        step_.thread_mode = RQ_THREADS_ALL;
        step_.hold_ms = spec.hold_ms;
        step_.time_budget_ms = k_step_budget_ms;
        step_.coverage_budget_bp = k_coverage_budget_bp;

        config_.struct_size = sizeof(rq_session_config);
        config_.step_def_size = sizeof(rq_step_def);
        config_.region_bytes = region_bytes;
        config_.min_region_bytes = config_.region_bytes;
        config_.seed = engine_seed;
        config_.worker_count = workers;
        config_.stop_on_error = 0U;
        config_.error_detail_cap = k_error_detail_cap;
        config_.progress_interval_ms = k_progress_interval_ms;
        config_.isa_override = RQ_ISA_AUTO;
        config_.step_count = 1U;
        config_.steps = &step_;
    }

    ~stop_session() {
        // A worker the handshake still holds would keep session_destroy's join waiting out its
        // bound, so a failed assertion's early return releases it here first.
        trigger_.release();
        if (handle_ != nullptr) {
            session_destroy(handle_);
        }
    }

    stop_session(const stop_session&) = delete;
    stop_session& operator=(const stop_session&) = delete;

    /**
     * Production seams a case replaces. A null affinity pins through win32_thread_affinity and
     * an empty free_override frees with VirtualFree; both are shared with the session, which
     * keeps them as long as session_dependencies requires.
     */
    struct seams {
        std::shared_ptr<thread_affinity> affinity;
        std::function<bool(void* address)> free_override;
    };

    /** session_create with the production clock, the given seams and a counting layer. */
    rq_result create(seams overrides = {}) {
        session_dependencies dependencies;
        dependencies.affinity = std::move(overrides.affinity);
        dependencies.free_override = std::move(overrides.free_override);
        dependencies.wrap_memory_access =
            [this](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
            auto counting = std::make_unique<counting_memory_access>(std::move(real));
            phase_trigger* const trigger = &trigger_;
            counting->set_observer(
                [trigger](const primitive_record& record) noexcept { trigger->observe(record); });
            layer_ = counting.get();
            return std::unique_ptr<IMemoryAccess>(std::move(counting));
        };
        const rq_callbacks callbacks = recorder_.callbacks();
        return session_create(&config_, &callbacks, std::move(dependencies), &handle_);
    }

    rq_result start() { return session_start(handle_); }

    /** The stop request: its QPC instant is taken on this thread just before the call. */
    rq_result stop_now(std::uint64_t& stop_ns) {
        stop_ns = qpc_now_ns();
        return session_stop(handle_, RQ_STOP_USER);
    }

    rq_result destroy() {
        const rq_result result = session_destroy(handle_);
        handle_ = nullptr;
        return result;
    }

    const rq_session_config& config() const noexcept { return config_; }
    run_recorder& recorder() noexcept { return recorder_; }
    phase_trigger& trigger() noexcept { return trigger_; }

    /** Primitives issued through the counting layer; exact once Completed has been raised. */
    std::uint64_t primitive_count() const noexcept {
        return layer_ != nullptr ? layer_->primitive_count() : 0U;
    }

private:
    rq_step_def step_{};
    rq_session_config config_{};
    run_recorder recorder_;
    phase_trigger trigger_;
    // Owned by the session; valid until the session is destroyed.
    counting_memory_access* layer_ = nullptr;
    rq_session_handle handle_ = nullptr;
};

/* ---------------------------------------------------------------------------------------------
 * Fixture
 * ------------------------------------------------------------------------------------------- */

using stop_param = std::tuple<stop_case, int>;

class stop_latency : public ::testing::TestWithParam<stop_param> {
protected:
    /** Resolves and records the suite seed once for every case. */
    static void SetUpTestSuite() {
        suite_seed_ = resolve_suite_seed();
        max_latency_ns_ = 0;
        max_latency_label_.clear();
        measurements_ = 0;
        RecordProperty("suite_seed", hex64(suite_seed_));
        std::printf("[ stop     ] suite seed %s (replay with %s)\n", hex64(suite_seed_).c_str(),
                    k_seed_variable);
        std::fflush(stdout);
    }

    /** The largest stop-to-Completed latency of the run, as a suite property and a line. */
    static void TearDownTestSuite() {
        if (measurements_ == 0U) {
            return;
        }
        RecordProperty("max_stop_latency_ms", format_ms(max_latency_ns_));
        RecordProperty("max_stop_latency_case", max_latency_label_);
        std::printf("[ stop     ] max stop -> Completed latency %s ms (%s) over %u stops; bound "
                    "%s ms\n",
                    format_ms(max_latency_ns_).c_str(), max_latency_label_.c_str(), measurements_,
                    format_ms(k_stop_bound_ns).c_str());
        std::fflush(stdout);
    }

    /**
     * Precondition: the region must be lockable without starving the machine. Fails rather
     * than skips, so a short machine never reports a pass it did not measure.
     */
    void SetUp() override {
        rq_system_info info{};
        info.struct_size = sizeof(rq_system_info);
        ASSERT_EQ(query_system(&info), RQ_OK);
        ASSERT_GE(info.avail_phys_bytes, k_min_available_bytes)
            << "Precondition failed: the stop-latency tests lock a 1 GiB region and need at "
               "least 2 GiB of available physical memory, but only "
            << info.avail_phys_bytes / k_mib << " MiB is available. Close other applications.";
        const std::size_t processors = enumerate_logical_processors().size();
        workers_ = static_cast<std::uint32_t>(std::min<std::size_t>(processors, k_max_workers));
        ASSERT_GT(workers_, 0U);
    }

    void run_preparing_case(const case_spec& spec, const std::string& label, std::mt19937_64& rng);
    void run_traffic_case(const case_spec& spec, const std::string& label, std::mt19937_64& rng);

    /** Checks shared by every stop: reason, bound, event order and full release. */
    static void expect_stopped_within_bound(const run_record& record, std::uint64_t stop_ns,
                                            const std::string& label);

    /** Records one stop's latency and release time as XML properties and a log line. */
    static void report(const std::string& label, std::uint32_t delay_ms, std::uint64_t stop_ns,
                       const run_record& record);

    static inline std::uint64_t suite_seed_ = 0;
    static inline std::uint64_t max_latency_ns_ = 0;
    static inline std::string max_latency_label_;
    static inline std::uint32_t measurements_ = 0;

    std::uint32_t workers_ = 0;
};

void stop_latency::expect_stopped_within_bound(const run_record& record, std::uint64_t stop_ns,
                                               const std::string& label) {
    EXPECT_EQ(record.result.end_reason, RQ_END_USER_STOP) << label << ": " << record.describe();
    const std::uint64_t latency_ns = since(record.completed_ns, stop_ns);
    EXPECT_GE(record.completed_ns, stop_ns) << label << ": Completed preceded the stop request";
    EXPECT_LE(latency_ns, k_stop_bound_ns)
        << label << ": Completed arrived " << format_ms(latency_ns)
        << " ms after the stop request; the bound is 2 s";
    EXPECT_EQ(record.completed_calls, 1U) << label;
    EXPECT_EQ(record.released_calls, 1U) << label;
    EXPECT_GE(record.released_ns, record.completed_ns) << label << ": Released preceded Completed";
    if (record.prepared()) {
        // Every chunk the run locked is released, whatever the phase the stop met.
        EXPECT_EQ(record.released_bytes, record.prepared_info.tested_bytes) << label;
    }
}

void stop_latency::report(const std::string& label, std::uint32_t delay_ms, std::uint64_t stop_ns,
                          const run_record& record) {
    const std::uint64_t latency_ns = since(record.completed_ns, stop_ns);
    // Release time is measured and logged only: it grows with the region, and the 2 s bound
    // ends at Completed.
    RecordProperty("stop_delay_ms_" + label, delay_ms);
    RecordProperty("stop_latency_ms_" + label, format_ms(latency_ns));
    RecordProperty("release_ms_" + label, record.release_ms);
    RecordProperty("released_bytes_" + label, record.released_bytes);
    std::printf("[ stop     ] %s: delay %u ms, stop -> Completed %s ms, release %llu ms "
                "(%llu MiB)\n",
                label.c_str(), delay_ms, format_ms(latency_ns).c_str(),
                static_cast<unsigned long long>(record.release_ms),
                static_cast<unsigned long long>(record.released_bytes / k_mib));
    std::fflush(stdout);

    ++measurements_;
    if (latency_ns >= max_latency_ns_) {
        max_latency_ns_ = latency_ns;
        max_latency_label_ = label;
    }
}

void stop_latency::run_preparing_case(const case_spec& spec, const std::string& label,
                                      std::mt19937_64& rng) {
    std::uint32_t max_delay_ms = spec.max_delay_ms;
    for (std::uint32_t attempt = 0; attempt < k_prepare_attempts; ++attempt) {
        const std::string attempt_label = label + "_attempt" + std::to_string(attempt);
        stop_session run(spec, workers_, rng());
        ASSERT_EQ(run.create(), RQ_OK) << attempt_label;
        const std::uint32_t delay_ms = random_delay_ms(rng, max_delay_ms);
        ASSERT_EQ(run.start(), RQ_OK) << attempt_label;
        Sleep(delay_ms);
        std::uint64_t stop_ns = 0;
        ASSERT_EQ(run.stop_now(stop_ns), RQ_OK) << attempt_label;

        ASSERT_TRUE(run.recorder().wait_completed(k_completed_timeout))
            << attempt_label << ": no Completed within 10 s of the stop; "
            << run.recorder().snapshot().describe();
        ASSERT_TRUE(run.recorder().wait_released(k_released_timeout))
            << attempt_label << ": no Released; " << run.recorder().snapshot().describe();
        const run_record record = run.recorder().snapshot();
        const std::uint64_t primitives = run.primitive_count();
        ASSERT_EQ(run.destroy(), RQ_OK) << attempt_label;

        expect_stopped_within_bound(record, stop_ns, attempt_label);
        report(attempt_label, delay_ms, stop_ns, record);
        if (!record.prepared()) {
            // The stop met preparation: no step gate, no step result and no memory traffic.
            // The chunks locked before the stop are held through Completed and released after
            // it, so released_bytes counts them: anything from 0, for a stop before the first
            // chunk, up to the region.
            EXPECT_EQ(record.gates, 0U) << attempt_label;
            EXPECT_EQ(record.result.step_result_count, 0U) << attempt_label;
            EXPECT_EQ(primitives, 0U) << attempt_label << ": a stop during preparation ran traffic";
            EXPECT_LE(record.released_bytes, run.config().region_bytes) << attempt_label;
            RecordProperty("preparing_attempts", attempt + 1U);
            return;
        }
        // Preparation finished before the stop, which then met the step. Aim the next attempt
        // at under half of both this delay and the preparation time just measured.
        const std::uint64_t aim_ms =
            std::min<std::uint64_t>(delay_ms, record.prepared_info.prepare_ms) / 2U;
        max_delay_ms = static_cast<std::uint32_t>(aim_ms);
        std::printf("[ stop     ] %s: Prepared arrived first (preparation took %llu ms); next "
                    "delay at most %u ms\n",
                    attempt_label.c_str(),
                    static_cast<unsigned long long>(record.prepared_info.prepare_ms), max_delay_ms);
        std::fflush(stdout);
    }
    FAIL() << label << ": preparation finished before the stop in all " << k_prepare_attempts
           << " attempts, so no stop met the Preparing state";
}

void stop_latency::run_traffic_case(const case_spec& spec, const std::string& label,
                                    std::mt19937_64& rng) {
    const bool hold = spec.target == phase_kind::pre_hold_flush;
    stop_session run(spec, workers_, rng());
    ASSERT_EQ(run.create(), RQ_OK) << label;
    ASSERT_EQ(run.start(), RQ_OK) << label;

    // Preparation and the step's first primitives come first; a run that ends early, for
    // example for lack of lockable memory, shows its end reason here.
    ASSERT_TRUE(run.trigger().wait(k_phase_timeout_ms))
        << label << ": phase " << phase_name(spec.target) << " not reached within 30 s; "
        << run.recorder().snapshot().describe();
    const std::uint32_t delay_ms = random_delay_ms(rng, spec.max_delay_ms);
    Sleep(delay_ms);
    if (!hold) {
        // The sleep ends anywhere in the pass. The worker issuing the next primitive of the
        // target phase is held inside that call until the stop has been requested, so the stop
        // lands inside the phase however late this thread wakes.
        run.trigger().arm_handshake();
        ASSERT_TRUE(run.trigger().wait(k_phase_timeout_ms))
            << label << ": phase " << phase_name(spec.target) << " did not recur; "
            << run.recorder().snapshot().describe();
    }
    std::uint64_t stop_ns = 0;
    const rq_result stop_result = run.stop_now(stop_ns);
    // Only after the request, so the held worker resumes into a stopping session; at once, so
    // the stop -> Completed interval spends no more than this call on the handshake.
    run.trigger().release();
    ASSERT_EQ(stop_result, RQ_OK) << label;

    ASSERT_TRUE(run.recorder().wait_completed(k_completed_timeout))
        << label << ": no Completed within 10 s of the stop; "
        << run.recorder().snapshot().describe();
    ASSERT_TRUE(run.recorder().wait_released(k_released_timeout))
        << label << ": no Released; " << run.recorder().snapshot().describe();
    const run_record record = run.recorder().snapshot();
    const std::uint64_t primitives = run.primitive_count();
    const std::uint64_t post_hold_verifies = run.trigger().verify_primitives();
    const std::uint64_t phase_to_stop_ns = since(stop_ns, run.trigger().fired_ns());
    // Complete: the held worker resumed before the workers joined, and they joined before
    // Completed.
    const worker_pause pause = run.trigger().pause();
    ASSERT_EQ(run.destroy(), RQ_OK) << label;

    expect_stopped_within_bound(record, stop_ns, label);
    // The stop landed inside the step: it was prepared, its gate continued, and its single
    // result is cut short.
    EXPECT_TRUE(record.prepared()) << label;
    EXPECT_EQ(record.prepared_info.tested_bytes, run.config().region_bytes) << label;
    EXPECT_EQ(record.gates, 1U) << label;
    ASSERT_TRUE(record.has_step_result) << label << ": " << record.describe();
    EXPECT_EQ(record.step_result.index, 0U) << label;
    EXPECT_EQ(record.step_result.completed, 0U) << label << ": the step ran to its schedule";
    EXPECT_GT(primitives, 0U) << label;
    if (hold) {
        // A stop inside the 30 s hold ends the pass before its verification.
        EXPECT_EQ(post_hold_verifies, 0U) << label << ": the stop landed after the hold";
    } else {
        // The request fell between the held worker's pause and its resumption, so a worker was
        // inside a primitive of the target phase when the stop was requested.
        EXPECT_TRUE(pause.paused) << label << ": no worker was held in phase "
                                  << phase_name(spec.target);
        EXPECT_TRUE(pause.released_by_test)
            << label << ": the held worker resumed on its " << k_phase_timeout_ms
            << " ms bound, not on the release after the stop";
        EXPECT_LE(pause.begin_ns, stop_ns)
            << label << ": the stop was requested before a worker was held in phase "
            << phase_name(spec.target);
        EXPECT_LE(stop_ns, pause.resume_ns)
            << label << ": the held worker resumed before the stop was requested";
        RecordProperty("phase_pause_ms_" + label,
                       format_ms(since(pause.resume_ns, pause.begin_ns)));
    }
    RecordProperty("phase_to_stop_ms_" + label, format_ms(phase_to_stop_ns));
    report(label, delay_ms, stop_ns, record);
}

/* ---------------------------------------------------------------------------------------------
 * Tests
 * ------------------------------------------------------------------------------------------- */

TEST_P(stop_latency, CompletedWithinTwoSecondsOfStop) {
    const auto& [kind, point] = GetParam();
    const case_spec& spec = spec_for(kind);
    const std::string label = std::string(spec.label) + "_" + std::to_string(point);
    const std::uint64_t salt = static_cast<std::uint64_t>(kind) * k_points_per_kind_u64 +
                               static_cast<std::uint64_t>(point);
    const std::uint64_t case_seed = mix_seed(suite_seed_, salt);
    RecordProperty("case_seed", hex64(case_seed));
    std::mt19937_64 rng(case_seed);

    if (kind == stop_case::preparing) {
        run_preparing_case(spec, label, rng);
    } else {
        run_traffic_case(spec, label, rng);
    }
}

/** Test-name suffix: the phase kind and the point, for example hold_2. */
std::string case_name(const ::testing::TestParamInfo<stop_param>& info) {
    return std::string(spec_for(std::get<0>(info.param)).label) + "_" +
           std::to_string(std::get<1>(info.param));
}

const auto k_all_cases =
    ::testing::Values(stop_case::preparing, stop_case::windowed_write, stop_case::windowed_verify,
                      stop_case::hold, stop_case::moving_inversion, stop_case::turnaround);

INSTANTIATE_TEST_SUITE_P(StopAtRandomPoints, stop_latency,
                         ::testing::Combine(k_all_cases, ::testing::Range(0, k_points_per_kind)),
                         case_name);

/* ---------------------------------------------------------------------------------------------
 * Stop on the first confirmed error
 * ------------------------------------------------------------------------------------------- */

/** Where the first injected error is confirmed: each phase kind that verifies. */
enum class first_error_phase : std::uint32_t {
    windowed_verify,
    hold_verify,
    moving_inversion,
    turnaround,
};

/** One verifying phase kind: the single step that runs it and the access type it verifies with. */
struct first_error_spec {
    first_error_phase phase;
    /** Test-name fragment and XML property suffix. */
    const char* label;
    const char* step_name;
    std::uint32_t pattern;
    /** Moving inversion ignores the order, but validation still requires a valid one. */
    std::uint32_t access_order;
    std::uint32_t hold_ms;
    /** RQ_ACCESS_* value of the phase's verifications, which the confirming re-read keeps. */
    std::uint32_t access_type;
};

/** Hold of the hold case: a real hold before the verification, short enough to keep runs quick. */
constexpr std::uint32_t k_first_error_hold_ms = 1'000U;

constexpr std::array<first_error_spec, 4> k_first_error_specs{{
    {first_error_phase::windowed_verify, "windowed_verify", "First error in windowed verify",
     RQ_PATTERN_RANDOM, RQ_ORDER_SEQUENTIAL_ASCENDING, 0U, RQ_ACCESS_VERIFY_AFTER_WRITE},
    {first_error_phase::hold_verify, "hold_verify", "First error after a refresh hold",
     RQ_PATTERN_RANDOM, RQ_ORDER_RANDOM_BLOCKS, k_first_error_hold_ms, RQ_ACCESS_VERIFY_AFTER_HOLD},
    {first_error_phase::moving_inversion, "moving_inversion", "First error in moving inversion",
     RQ_PATTERN_MOVING_INVERSION_WALKING, RQ_ORDER_SEQUENTIAL_ASCENDING, 0U,
     RQ_ACCESS_READ_MODIFY_WRITE},
    {first_error_phase::turnaround, "turnaround", "First error in turnaround", RQ_PATTERN_RANDOM,
     RQ_ORDER_TURNAROUND, 0U, RQ_ACCESS_TURNAROUND_READ},
}};

constexpr bool first_error_specs_in_enum_order() noexcept {
    for (std::size_t i = 0; i < k_first_error_specs.size(); ++i) {
        if (static_cast<std::size_t>(k_first_error_specs[i].phase) != i) {
            return false;
        }
    }
    return true;
}
static_assert(first_error_specs_in_enum_order(),
              "k_first_error_specs is indexed by first_error_phase");

const first_error_spec& first_error_spec_for(first_error_phase phase) noexcept {
    return k_first_error_specs[static_cast<std::size_t>(phase)];
}

/** gtest prints parameters through this, so failures name the case instead of raw bytes. */
void PrintTo(first_error_phase phase, std::ostream* os) {
    *os << first_error_spec_for(phase).label;
}

/** Runs per phase kind, each with its own seeds; an int because ::testing::Range generates them. */
constexpr int k_first_error_runs_per_kind = 3;
constexpr std::uint64_t k_first_error_runs_per_kind_u64 =
    static_cast<std::uint64_t>(k_first_error_runs_per_kind);

/**
 * The injected fault: the single-bit-flip class of the verification plan, bit 17 in a few seeded
 * words of every pass, so the first pass's verification meets a corrupted word. The first one met
 * ends the run; the cap only bounds the further flips workers still verify before they see the
 * stop.
 */
constexpr std::uint32_t k_first_error_bit = 17U;
constexpr std::uint32_t k_first_error_flips_per_pass = 4U;
constexpr std::uint64_t k_first_error_max_injections = 8U;

/** How error_collector's detail begins for an error in the first pass of a one-step plan. */
constexpr char k_first_error_detail_prefix[] = "First error: step 1 of 1, pass 1, ";

/**
 * Start to on_completed: the phase wait, for preparation and the first pass to reach the error,
 * then the Completed wait. It only keeps a broken engine from hanging the suite; the gate is the
 * measured interval.
 */
constexpr std::chrono::milliseconds k_first_error_completed_timeout =
    std::chrono::milliseconds{k_phase_timeout_ms} + k_completed_timeout;

/** The bit an RQ_ACCESS_* value sets in confirmation_probe::access_types(); 31 from 31 up. */
constexpr std::uint32_t access_type_bit(std::uint32_t access_type) noexcept {
    return std::uint32_t{1} << (access_type < 31U ? access_type : 31U);
}

/**
 * The counting layer's observer of a first-error run. error_collector re-reads every mismatch
 * once through the outermost layer, then classifies it and, for the first one, requests
 * RQ_END_FIRST_ERROR, so the earliest re-read comes no later than the confirmation that stops.
 *
 * observe() runs on worker threads after each primitive has been forwarded, so a re-read's stamp
 * follows its flushed load and precedes the stop request that may follow it. It never calls a
 * session function, never blocks and never throws: it reads the clock and updates atomics. The
 * test thread reads them after on_completed, which every worker's join precedes, so relaxed
 * ordering suffices.
 */
class confirmation_probe {
public:
    confirmation_probe() = default;
    confirmation_probe(const confirmation_probe&) = delete;
    confirmation_probe& operator=(const confirmation_probe&) = delete;

    void observe(const primitive_record& record) noexcept {
        if (record.kind != primitive_kind::reread_word || record.ctx.phase != phase_kind::reread) {
            return;
        }
        const std::uint64_t now = qpc_now_ns();
        rereads_.fetch_add(1U, std::memory_order_relaxed);
        access_types_.fetch_or(access_type_bit(record.ctx.access_type), std::memory_order_relaxed);
        // Workers re-read concurrently, so the earliest instant is kept, not the first stored.
        std::uint64_t earliest = earliest_ns_.load(std::memory_order_relaxed);
        while ((earliest == 0U || now < earliest) &&
               !earliest_ns_.compare_exchange_weak(earliest, now, std::memory_order_relaxed)) {
            // A failed exchange reloaded earliest; retry only while this re-read is earlier.
        }
    }

    /** QPC instant of the earliest re-read; 0 before the first. */
    std::uint64_t earliest_confirmation_ns() const noexcept {
        return earliest_ns_.load(std::memory_order_relaxed);
    }

    /** Re-reads issued so far. */
    std::uint64_t rereads() const noexcept { return rereads_.load(std::memory_order_relaxed); }

    /** access_type_bit of every re-read's access type, or-ed together. */
    std::uint32_t access_types() const noexcept {
        return access_types_.load(std::memory_order_relaxed);
    }

private:
    std::atomic<std::uint64_t> earliest_ns_{0};
    std::atomic<std::uint64_t> rereads_{0};
    std::atomic<std::uint32_t> access_types_{0};
};

/**
 * One production session with Stop on error on over fault-injecting -> counting -> real: its
 * single-step plan, its fault spec, its recorder and its probe. It offers no stop, so only the
 * confirming worker can end the run early. The destructor destroys the session first, which
 * joins the coordinator and guarantees no callback or primitive runs after it, so the recorder,
 * the probe and the clock outlive every engine reference to them on every exit path.
 */
class first_error_session {
public:
    first_error_session(const first_error_spec& spec, std::uint32_t workers,
                        std::uint64_t engine_seed, std::uint64_t fault_seed)
        : clock_(std::make_shared<qpc_clock>()) {
        copy_utf8_truncated(step_.name, sizeof(step_.name), spec.step_name);
        step_.pattern = spec.pattern;
        step_.access_order = spec.access_order;
        step_.thread_mode = RQ_THREADS_ALL;
        step_.hold_ms = spec.hold_ms;
        // Far beyond the first pass, so only the first confirmed error can end the step.
        step_.time_budget_ms = k_step_budget_ms;
        step_.coverage_budget_bp = k_coverage_budget_bp;

        config_.struct_size = sizeof(rq_session_config);
        config_.step_def_size = sizeof(rq_step_def);
        config_.region_bytes = region_bytes_for(workers);
        config_.min_region_bytes = config_.region_bytes;
        config_.seed = engine_seed;
        config_.worker_count = workers;
        config_.stop_on_error = 1U;
        config_.error_detail_cap = k_error_detail_cap;
        config_.progress_interval_ms = k_progress_interval_ms;
        config_.isa_override = RQ_ISA_AUTO;
        config_.step_count = 1U;
        config_.steps = &step_;

        rq_fault_spec fault = make_fault_spec(RQ_FAULT_SINGLE_BIT_FLIP);
        fault.bit = k_first_error_bit;
        fault.per_pass_count = k_first_error_flips_per_pass;
        fault.max_injections = k_first_error_max_injections;
        fault.seed = fault_seed;
        fault_ = normalized(fault, config_.region_bytes);
    }

    ~first_error_session() {
        if (handle_ != nullptr) {
            session_destroy(handle_);
        }
    }

    first_error_session(const first_error_session&) = delete;
    first_error_session& operator=(const first_error_session&) = delete;

    /**
     * session_create with the production affinity, fault-injecting -> counting -> real, and one
     * qpc_clock for the session and the fault layer, as rq_test_session_create builds them.
     */
    rq_result create() {
        session_dependencies dependencies;
        dependencies.clock = clock_;
        dependencies.wrap_memory_access =
            [this](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
            auto counting = std::make_unique<counting_memory_access>(std::move(real));
            confirmation_probe* const probe = &probe_;
            counting->set_observer(
                [probe](const primitive_record& record) noexcept { probe->observe(record); });
            auto injecting = std::make_unique<fault_injecting_memory_access>(std::move(counting),
                                                                             fault_, *clock_);
            fault_layer_ = injecting.get();
            return std::unique_ptr<IMemoryAccess>(std::move(injecting));
        };
        const rq_callbacks callbacks = recorder_.callbacks();
        return session_create(&config_, &callbacks, std::move(dependencies), &handle_);
    }

    rq_result start() { return session_start(handle_); }

    rq_result destroy() {
        const rq_result result = session_destroy(handle_);
        handle_ = nullptr;
        return result;
    }

    const rq_session_config& config() const noexcept { return config_; }
    const rq_fault_spec& fault_spec() const noexcept { return fault_; }
    run_recorder& recorder() noexcept { return recorder_; }
    const confirmation_probe& probe() const noexcept { return probe_; }

    /** Logged injections whose verification ran; exact once Completed has been raised. */
    std::uint64_t verified_injections() const noexcept {
        return fault_layer_ != nullptr ? fault_layer_->verified_opportunity_count() : 0U;
    }

private:
    std::shared_ptr<IClock> clock_;
    rq_step_def step_{};
    rq_session_config config_{};
    rq_fault_spec fault_{};
    run_recorder recorder_;
    confirmation_probe probe_;
    // Owned by the session; valid until the session is destroyed.
    fault_injecting_memory_access* fault_layer_ = nullptr;
    rq_session_handle handle_ = nullptr;
};

using first_error_param = std::tuple<first_error_phase, int>;

class first_error_latency : public ::testing::TestWithParam<first_error_param> {
protected:
    /** Resolves and records this suite's seed once for every run, as stop_latency does its own. */
    static void SetUpTestSuite() {
        suite_seed_ = resolve_suite_seed();
        max_latency_ns_ = 0;
        max_latency_label_.clear();
        measurements_ = 0;
        RecordProperty("suite_seed", hex64(suite_seed_));
        std::printf("[ stop     ] first-error suite seed %s (replay with %s)\n",
                    hex64(suite_seed_).c_str(), k_seed_variable);
        std::fflush(stdout);
    }

    /** The largest first-error-to-Completed latency of the run, as a suite property and a line. */
    static void TearDownTestSuite() {
        if (measurements_ == 0U) {
            return;
        }
        RecordProperty("max_first_error_latency_ms", format_ms(max_latency_ns_));
        RecordProperty("max_first_error_latency_case", max_latency_label_);
        std::printf(
            "[ stop     ] max first confirmed error -> Completed latency %s ms (%s) over %u "
            "runs; bound %s ms\n",
            format_ms(max_latency_ns_).c_str(), max_latency_label_.c_str(), measurements_,
            format_ms(k_stop_bound_ns).c_str());
        std::fflush(stdout);
    }

    /** stop_latency's precondition, for the same 1 GiB region: fails rather than skips. */
    void SetUp() override {
        rq_system_info info{};
        info.struct_size = sizeof(rq_system_info);
        ASSERT_EQ(query_system(&info), RQ_OK);
        ASSERT_GE(info.avail_phys_bytes, k_min_available_bytes)
            << "Precondition failed: the first-error latency tests lock a 1 GiB region and need "
               "at least 2 GiB of available physical memory, but only "
            << info.avail_phys_bytes / k_mib << " MiB is available. Close other applications.";
        const std::size_t processors = enumerate_logical_processors().size();
        workers_ = static_cast<std::uint32_t>(std::min<std::size_t>(processors, k_max_workers));
        ASSERT_GT(workers_, 0U);
    }

    /** Records one run's latency, errors and release time as XML properties and a log line. */
    static void report(const std::string& label, std::uint64_t confirmed_ns,
                       const run_record& record);

    static inline std::uint64_t suite_seed_ = 0;
    static inline std::uint64_t max_latency_ns_ = 0;
    static inline std::string max_latency_label_;
    static inline std::uint32_t measurements_ = 0;

    std::uint32_t workers_ = 0;
};

void first_error_latency::report(const std::string& label, std::uint64_t confirmed_ns,
                                 const run_record& record) {
    const std::uint64_t latency_ns = since(record.completed_ns, confirmed_ns);
    // Release time is measured and logged only: it grows with the region, and the 2 s bound
    // ends at Completed.
    RecordProperty("first_error_latency_ms_" + label, format_ms(latency_ns));
    RecordProperty("total_errors_" + label, record.result.total_errors);
    RecordProperty("release_ms_" + label, record.release_ms);
    RecordProperty("released_bytes_" + label, record.released_bytes);
    std::printf("[ stop     ] %s: first confirmed error -> Completed %s ms, %llu errors, release "
                "%llu ms (%llu MiB)\n",
                label.c_str(), format_ms(latency_ns).c_str(),
                static_cast<unsigned long long>(record.result.total_errors),
                static_cast<unsigned long long>(record.release_ms),
                static_cast<unsigned long long>(record.released_bytes / k_mib));
    std::fflush(stdout);

    ++measurements_;
    if (latency_ns >= max_latency_ns_) {
        max_latency_ns_ = latency_ns;
        max_latency_label_ = label;
    }
}

TEST_P(first_error_latency, CompletedWithinTwoSecondsOfTheFirstConfirmedError) {
    const auto& [phase, run_index] = GetParam();
    const first_error_spec& spec = first_error_spec_for(phase);
    const std::string label = std::string(spec.label) + "_" + std::to_string(run_index);
    const std::uint64_t salt = static_cast<std::uint64_t>(phase) * k_first_error_runs_per_kind_u64 +
                               static_cast<std::uint64_t>(run_index);
    const std::uint64_t case_seed = mix_seed(suite_seed_, salt);
    RecordProperty("case_seed", hex64(case_seed));
    std::mt19937_64 rng(case_seed);
    const std::uint64_t engine_seed = rng();
    const std::uint64_t fault_seed = rng();

    first_error_session run(spec, workers_, engine_seed, fault_seed);
    const char* invalid = nullptr;
    ASSERT_TRUE(validate_fault_spec(run.fault_spec(), run.config().region_bytes, &invalid))
        << label << ": " << invalid;
    ASSERT_EQ(run.create(), RQ_OK) << label;
    ASSERT_EQ(run.start(), RQ_OK) << label;

    ASSERT_TRUE(run.recorder().wait_completed(k_first_error_completed_timeout))
        << label << ": no Completed within " << k_first_error_completed_timeout.count()
        << " ms of the start, so no confirmed error cut the " << k_step_budget_ms
        << " ms step short; re-reads=" << run.probe().rereads() << " "
        << run.recorder().snapshot().describe();
    ASSERT_TRUE(run.recorder().wait_released(k_released_timeout))
        << label << ": no Released; " << run.recorder().snapshot().describe();
    const run_record record = run.recorder().snapshot();
    const std::uint64_t confirmed_ns = run.probe().earliest_confirmation_ns();
    const std::uint64_t rereads = run.probe().rereads();
    const std::uint32_t access_types = run.probe().access_types();
    const std::uint64_t verified_injections = run.verified_injections();
    ASSERT_EQ(run.destroy(), RQ_OK) << label;

    // The confirming worker ended the run. No host stop was requested, and the first reason
    // recorded decides the end, so any earlier stop would show here instead.
    EXPECT_EQ(record.result.end_reason, RQ_END_FIRST_ERROR) << label << ": " << record.describe();
    EXPECT_EQ(record.end_detail().rfind(k_first_error_detail_prefix, 0), 0U)
        << label << ": end_detail '" << record.end_detail() << "' does not begin '"
        << k_first_error_detail_prefix << "'";
    EXPECT_GE(record.result.total_errors, 1U) << label;
    EXPECT_NE(record.result.first_error_ms, RQ_NO_FIRST_ERROR) << label;
    EXPECT_EQ(record.result.steps_completed, 0U) << label;

    // The error met the step: it was prepared on the whole region, its gate continued, and its
    // single result is cut short and carries every error.
    EXPECT_TRUE(record.prepared()) << label;
    EXPECT_EQ(record.prepared_info.tested_bytes, run.config().region_bytes) << label;
    EXPECT_EQ(record.gates, 1U) << label;
    ASSERT_TRUE(record.has_step_result) << label << ": " << record.describe();
    EXPECT_EQ(record.step_result.index, 0U) << label;
    EXPECT_EQ(record.step_result.completed, 0U) << label << ": the step ran to its schedule";
    EXPECT_EQ(record.step_result.errors, record.result.total_errors) << label;

    // Every error is an injected flip whose verification ran, re-read once in the phase kind
    // under test, so the earliest re-read is no later than the confirmation that stopped the run.
    EXPECT_GE(verified_injections, 1U) << label << ": the fault layer logged no verified injection";
    EXPECT_EQ(record.result.total_errors, verified_injections) << label;
    EXPECT_EQ(rereads, record.result.total_errors) << label;
    EXPECT_EQ(access_types, access_type_bit(spec.access_type))
        << label << ": the re-reads carried access-type mask " << access_types;

    // The bound under test: the earliest confirmed error to on_completed.
    ASSERT_NE(confirmed_ns, 0U) << label << ": no re-read confirmed an error";
    const std::uint64_t latency_ns = since(record.completed_ns, confirmed_ns);
    EXPECT_GE(record.completed_ns, confirmed_ns)
        << label << ": Completed preceded the first confirmed error";
    EXPECT_LE(latency_ns, k_stop_bound_ns)
        << label << ": Completed arrived " << format_ms(latency_ns)
        << " ms after the first confirmed error; the bound is 2 s";

    // Completed and Released once each, in that order, and every locked byte released.
    EXPECT_EQ(record.completed_calls, 1U) << label;
    EXPECT_EQ(record.released_calls, 1U) << label;
    EXPECT_GE(record.released_ns, record.completed_ns) << label << ": Released preceded Completed";
    EXPECT_EQ(record.released_bytes, record.prepared_info.tested_bytes) << label;
    report(label, confirmed_ns, record);
}

/** Test-name suffix: the phase kind and the run, for example turnaround_2. */
std::string first_error_case_name(const ::testing::TestParamInfo<first_error_param>& info) {
    return std::string(first_error_spec_for(std::get<0>(info.param)).label) + "_" +
           std::to_string(std::get<1>(info.param));
}

const auto k_first_error_phases =
    ::testing::Values(first_error_phase::windowed_verify, first_error_phase::hold_verify,
                      first_error_phase::moving_inversion, first_error_phase::turnaround);

INSTANTIATE_TEST_SUITE_P(StopOnFirstError, first_error_latency,
                         ::testing::Combine(k_first_error_phases,
                                            ::testing::Range(0, k_first_error_runs_per_kind)),
                         first_error_case_name);

/**
 * The preparation-release case's affinity seam. The worker pool starts only once allocation has
 * locked the whole region, and its start waits for every worker's pin, so holding one pin holds
 * preparation where every chunk is locked and no traffic can have run.
 *
 * The first pin(), on whichever worker calls first, stamps the QPC instant, signals the test
 * thread and waits, bounded by k_phase_timeout_ms, until open(). Every pin, the held one included
 * once it is let go, then pins through win32_thread_affinity, so the workers really are pinned and
 * the run ends as a production one would. Workers call pin() concurrently: the hold is claimed
 * with one atomic exchange, and its outcome is kept in atomics the test thread reads.
 */
class preparation_gate final : public thread_affinity {
public:
    /** Opens the gate on every exit from its scope, a failed assertion's early return included. */
    class open_on_exit {
    public:
        explicit open_on_exit(preparation_gate& gate) noexcept : gate_(gate) {}
        ~open_on_exit() { gate_.open(); }
        open_on_exit(const open_on_exit&) = delete;
        open_on_exit& operator=(const open_on_exit&) = delete;

    private:
        preparation_gate& gate_;
    };

    /** @throws std::system_error when an event cannot be created. */
    preparation_gate() = default;
    preparation_gate(const preparation_gate&) = delete;
    preparation_gate& operator=(const preparation_gate&) = delete;

    std::uint32_t pin(std::uint32_t worker_index, std::uint16_t group,
                      std::uint8_t processor) override {
        if (!claimed_.exchange(true, std::memory_order_acq_rel)) {
            hold();
        }
        return production_.pin(worker_index, group, processor);
    }

    /** Waits for a worker to reach the hold; true when one did within timeout_ms. */
    bool wait_held(DWORD timeout_ms) const noexcept {
        return WaitForSingleObject(held_event_.get(), timeout_ms) == WAIT_OBJECT_0;
    }

    /** Lets the held pin go on. Idempotent, and effective before the hold too. */
    void open() noexcept {
        if (!SetEvent(open_event_.get())) {
            note_error(GetLastError());
        }
    }

    /** QPC instant at which a worker reached the hold; 0 while none has. */
    std::uint64_t held_ns() const noexcept { return held_ns_.load(std::memory_order_acquire); }

    /** QPC instant at which the held pin went on; 0 while it is held or none was. */
    std::uint64_t let_go_ns() const noexcept { return let_go_ns_.load(std::memory_order_acquire); }

    /**
     * How the held pin's wait ended: WAIT_OBJECT_0 when open() let it go, WAIT_TIMEOUT when the
     * bound ran out first, and WAIT_FAILED when the wait failed or none has ended yet.
     */
    DWORD wait_result() const noexcept { return wait_result_.load(std::memory_order_acquire); }

    /** Windows error of the first failed SetEvent or wait; ERROR_SUCCESS when none failed. */
    DWORD error() const noexcept { return error_.load(std::memory_order_acquire); }

private:
    void hold() noexcept {
        held_ns_.store(qpc_now_ns(), std::memory_order_release);
        if (!SetEvent(held_event_.get())) {
            note_error(GetLastError());
        }
        const DWORD waited = WaitForSingleObject(open_event_.get(), k_phase_timeout_ms);
        if (waited == WAIT_FAILED) {
            note_error(GetLastError());
        }
        let_go_ns_.store(qpc_now_ns(), std::memory_order_release);
        wait_result_.store(waited, std::memory_order_release);
    }

    void note_error(DWORD error) noexcept {
        DWORD none = ERROR_SUCCESS;
        error_.compare_exchange_strong(none, error, std::memory_order_acq_rel);
    }

    win32_thread_affinity production_;
    // The test thread is the held event's only waiter. The open event is manual-reset, so the
    // held pin passes however open() and the hold are ordered, and opening twice is harmless.
    event_handle held_event_{event_reset::automatic};
    event_handle open_event_{event_reset::manual};
    std::atomic<bool> claimed_{false};
    std::atomic<std::uint64_t> held_ns_{0};
    std::atomic<std::uint64_t> let_go_ns_{0};
    std::atomic<DWORD> wait_result_{WAIT_FAILED};
    std::atomic<DWORD> error_{ERROR_SUCCESS};
};

/**
 * The preparation-release case's free seam: frees each chunk with VirtualFree, as production
 * does, but only after holding it for a fixed delay, so releasing the region outlasts the stop
 * bound however fast the machine frees memory. Each call stamps the QPC instant it began.
 *
 * The region calls it on one thread at a time, but a chunk it refused would be retried from any
 * thread of the process, so each stamp has its own atomic slot, claimed through one atomic
 * index, and nothing here allocates, takes a lock or throws. The test reads the stamps once the
 * session is destroyed, which joins the coordinator that made the calls.
 */
class slow_chunk_free {
public:
    /** Room for capacity stamps; calls beyond it are counted but not stamped. */
    slow_chunk_free(std::size_t capacity, std::uint64_t delay_ns)
        : stamps_(capacity), delay_ns_(delay_ns) {}
    slow_chunk_free(const slow_chunk_free&) = delete;
    slow_chunk_free& operator=(const slow_chunk_free&) = delete;

    /**
     * The free_function contract: true once the chunk is freed; otherwise false, with the last
     * error of the failed VirtualFree, and the chunk left allocated.
     */
    bool free_chunk(void* address) noexcept {
        const std::uint64_t begin_ns = qpc_now_ns();
        const std::uint64_t call = calls_.fetch_add(1U, std::memory_order_relaxed);
        if (call < stamps_.size()) {
            stamps_[static_cast<std::size_t>(call)].store(begin_ns, std::memory_order_relaxed);
        }
        // Held on QPC rather than by one Sleep, which can end up to a timer tick early, so every
        // chunk adds at least the whole delay to the release.
        const std::uint64_t until_ns = begin_ns + delay_ns_;
        for (std::uint64_t now_ns = begin_ns; now_ns < until_ns; now_ns = qpc_now_ns()) {
            Sleep(static_cast<DWORD>((until_ns - now_ns + k_ns_per_ms - 1U) / k_ns_per_ms));
        }
        if (VirtualFree(address, 0, MEM_RELEASE) != FALSE) {
            return true;
        }
        const DWORD error = GetLastError();
        failures_.fetch_add(1U, std::memory_order_relaxed);
        DWORD none = ERROR_SUCCESS;
        first_error_.compare_exchange_strong(none, error, std::memory_order_relaxed);
        // The region reads why the chunk stayed allocated from the last error, as after a
        // failed VirtualFree of its own.
        SetLastError(error);
        return false;
    }

    /** Calls made so far, freed or not. */
    std::uint64_t calls() const noexcept { return calls_.load(std::memory_order_relaxed); }

    /** Calls whose VirtualFree failed. */
    std::uint64_t failures() const noexcept { return failures_.load(std::memory_order_relaxed); }

    /** Windows error of the first failed VirtualFree; ERROR_SUCCESS when none failed. */
    DWORD first_error() const noexcept { return first_error_.load(std::memory_order_relaxed); }

    /** QPC instants at which the stamped calls began, in call order. */
    std::vector<std::uint64_t> stamps() const {
        const std::size_t count =
            static_cast<std::size_t>(std::min<std::uint64_t>(calls(), stamps_.size()));
        std::vector<std::uint64_t> copy;
        copy.reserve(count);
        for (std::size_t i = 0; i < count; ++i) {
            copy.push_back(stamps_[i].load(std::memory_order_relaxed));
        }
        return copy;
    }

private:
    std::vector<std::atomic<std::uint64_t>> stamps_;
    const std::uint64_t delay_ns_;
    std::atomic<std::uint64_t> calls_{0};
    std::atomic<std::uint64_t> failures_{0};
    std::atomic<DWORD> first_error_{ERROR_SUCCESS};
};

/**
 * A stop during preparation with the whole region locked, and a release slower than the bound.
 * Its own suite, because gtest does not let TEST_F share the parameterised stop_latency fixture.
 */
class stop_latency_preparation_release : public ::testing::Test {
protected:
    /** stop_latency's precondition, as the case locks the same region: a failure, never a skip. */
    void SetUp() override {
        rq_system_info info{};
        info.struct_size = sizeof(rq_system_info);
        ASSERT_EQ(query_system(&info), RQ_OK);
        ASSERT_GE(info.avail_phys_bytes, k_min_available_bytes)
            << "Precondition failed: the stop-latency tests lock a 1 GiB region and need at "
               "least 2 GiB of available physical memory, but only "
            << info.avail_phys_bytes / k_mib << " MiB is available. Close other applications.";
        const std::size_t processors = enumerate_logical_processors().size();
        workers_ = static_cast<std::uint32_t>(std::min<std::size_t>(processors, k_max_workers));
        ASSERT_GT(workers_, 0U);
    }

    std::uint32_t workers_ = 0;
};

TEST_F(stop_latency_preparation_release, CompletedWithinTwoSecondsBeforeASlowRelease) {
    const std::uint64_t region_bytes = region_bytes_for(workers_);
    const region_plan plan = plan_region(region_bytes, workers_);
    const std::uint64_t chunk_count = std::uint64_t{plan.worker_count} * plan.chunks_per_slice;
    ASSERT_EQ(plan.tested_bytes, region_bytes)
        << region_bytes / k_mib << " MiB does not split into whole slices for " << workers_
        << " workers";
    ASSERT_GT(chunk_count, 0U);
    // Whole milliseconds per chunk, rounded up: the chunks together are held for at least
    // k_slow_release_ms, and release_ms, which truncates to milliseconds, can be held to it.
    const std::uint64_t chunk_delay_ms = (k_slow_release_ms + chunk_count - 1U) / chunk_count;
    const std::uint64_t slow_release_ms = chunk_delay_ms * chunk_count;

    const auto gate = std::make_shared<preparation_gate>();
    const auto frees = std::make_shared<slow_chunk_free>(static_cast<std::size_t>(chunk_count),
                                                         chunk_delay_ms * k_ns_per_ms);
    stop_session run(spec_for(stop_case::preparing), workers_, k_preparation_release_seed);
    // Destroyed before run, so a failed assertion lets the held worker go before
    // session_destroy joins the coordinator that waits for it, not after the gate's bound.
    const preparation_gate::open_on_exit opener(*gate);
    stop_session::seams overrides;
    overrides.affinity = gate;
    // Shares the recorder, so it outlives every copy of the function the engine keeps.
    overrides.free_override = [frees](void* address) noexcept {
        return frees->free_chunk(address);
    };
    ASSERT_EQ(run.create(std::move(overrides)), RQ_OK);
    ASSERT_EQ(run.start(), RQ_OK);

    // A worker reaches the hold only once allocation has locked every chunk; a run that ends
    // first, for example for lack of lockable memory, shows its end reason here.
    ASSERT_TRUE(gate->wait_held(k_phase_timeout_ms))
        << "no worker pin within 30 s of the start; " << run.recorder().snapshot().describe();
    std::uint64_t stop_ns = 0;
    ASSERT_EQ(run.stop_now(stop_ns), RQ_OK);
    gate->open();

    ASSERT_TRUE(run.recorder().wait_completed(k_completed_timeout))
        << "no Completed within 10 s of the stop; " << run.recorder().snapshot().describe();
    ASSERT_TRUE(run.recorder().wait_released(k_released_timeout))
        << "no Released; " << run.recorder().snapshot().describe() << "; " << frees->failures()
        << " chunk frees failed, the first with Windows error " << frees->first_error();
    const run_record record = run.recorder().snapshot();
    const std::uint64_t primitives = run.primitive_count();
    ASSERT_EQ(run.destroy(), RQ_OK);
    const std::vector<std::uint64_t> free_ns = frees->stamps();

    const std::uint64_t latency_ns = since(record.completed_ns, stop_ns);
    const std::string label = "preparation_release";
    RecordProperty("region_mib_" + label, region_bytes / k_mib);
    RecordProperty("chunks_" + label, chunk_count);
    RecordProperty("chunk_delay_ms_" + label, chunk_delay_ms);
    RecordProperty("stop_latency_ms_" + label, format_ms(latency_ns));
    RecordProperty("release_ms_" + label, record.release_ms);
    RecordProperty("released_bytes_" + label, record.released_bytes);
    std::printf("[ stop     ] %s: stop -> Completed %s ms, release %llu ms (%llu MiB in %llu "
                "chunks, at least %llu ms forced) after Completed\n",
                label.c_str(), format_ms(latency_ns).c_str(),
                static_cast<unsigned long long>(record.release_ms),
                static_cast<unsigned long long>(record.released_bytes / k_mib),
                static_cast<unsigned long long>(chunk_count),
                static_cast<unsigned long long>(slow_release_ms));
    std::fflush(stdout);

    // The stop met preparation held where every chunk is locked: no Prepared, no step gate, no
    // step result and no memory traffic.
    EXPECT_EQ(record.result.end_reason, RQ_END_USER_STOP) << record.describe();
    EXPECT_EQ(record.prepared_calls, 0U);
    EXPECT_EQ(record.gates, 0U);
    EXPECT_EQ(record.result.step_result_count, 0U);
    EXPECT_EQ(primitives, 0U) << "a stop during preparation ran traffic";

    // The test let the held pin go, after the stop, rather than the gate's bound running out.
    EXPECT_EQ(gate->wait_result(), WAIT_OBJECT_0)
        << "the held pin was not let go by the test; Windows error " << gate->error();
    EXPECT_LE(gate->held_ns(), stop_ns) << "the stop preceded the hold";
    EXPECT_GE(gate->let_go_ns(), stop_ns) << "the held pin went on before the stop";

    // The bound, although the release takes longer than it.
    EXPECT_EQ(record.completed_calls, 1U);
    EXPECT_GE(record.completed_ns, stop_ns) << "Completed preceded the stop request";
    EXPECT_LE(latency_ns, k_stop_bound_ns)
        << "Completed arrived " << format_ms(latency_ns)
        << " ms after a stop with the whole region locked; the bound is 2 s";

    // Every chunk is freed through the seam after Completed and before Released, so the release
    // outlasted the bound without delaying Completed, and Released reports the whole region.
    EXPECT_EQ(frees->calls(), chunk_count) << "frees through the seam, one per chunk of the region";
    EXPECT_EQ(frees->failures(), 0U) << "the first with Windows error " << frees->first_error();
    const auto freed_before_completed = std::count_if(
        free_ns.begin(), free_ns.end(), [&](std::uint64_t ns) { return ns < record.completed_ns; });
    const auto freed_after_released = std::count_if(
        free_ns.begin(), free_ns.end(), [&](std::uint64_t ns) { return ns > record.released_ns; });
    EXPECT_EQ(freed_before_completed, 0) << "chunks whose free began before Completed";
    EXPECT_EQ(freed_after_released, 0) << "chunks whose free began after Released";
    EXPECT_EQ(record.released_calls, 1U);
    EXPECT_GE(record.released_ns, record.completed_ns) << "Released preceded Completed";
    EXPECT_EQ(record.released_bytes, plan.tested_bytes)
        << "Released did not report the whole region locked before the stop";
    EXPECT_GE(record.release_ms, slow_release_ms)
        << "the release took less than the delay the free seam forces";
    EXPECT_GE(since(record.released_ns, record.completed_ns), slow_release_ms * k_ns_per_ms)
        << "Released followed Completed by less than the delay the free seam forces";
}

/* ---------------------------------------------------------------------------------------------
 * Checked handle close
 * ------------------------------------------------------------------------------------------- */

/**
 * Collects the lines the constructing thread writes with OutputDebugStringW while it lives.
 *
 * OutputDebugStringW raises DBG_PRINTEXCEPTION_WIDE_C, whose first two parameters are the line's
 * length including its terminator and its address, and handles that exception itself when no
 * debugger is attached. A first vectored handler sees it before then, so the line can be read in
 * process, without a debugger and without the DBWIN buffer that every process on the host shares.
 * The handler only copies the text, never allocates, and continues the search for every
 * exception, so the output is unchanged and a fault still ends the process as the engine's error
 * contract requires. Lines of other threads are ignored. One capture may exist at a time.
 */
class debugger_line_capture {
public:
    /** @throws std::runtime_error when the vectored handler cannot be registered. */
    debugger_line_capture() : thread_id_(GetCurrentThreadId()) {
        active_.store(this, std::memory_order_release);
        cookie_ = AddVectoredExceptionHandler(1, &debugger_line_capture::on_exception);
        if (cookie_ == nullptr) {
            active_.store(nullptr, std::memory_order_release);
            throw std::runtime_error("AddVectoredExceptionHandler failed");
        }
    }
    ~debugger_line_capture() {
        RemoveVectoredExceptionHandler(cookie_);
        active_.store(nullptr, std::memory_order_release);
    }
    debugger_line_capture(const debugger_line_capture&) = delete;
    debugger_line_capture& operator=(const debugger_line_capture&) = delete;

    /** Every line written so far; one beyond k_max_lines is counted as an empty entry. */
    std::vector<std::wstring> lines() const {
        std::vector<std::wstring> result;
        for (std::size_t i = 0; i < count_; ++i) {
            result.emplace_back(i < k_max_lines ? std::wstring(text_[i].data(), length_[i])
                                                : std::wstring());
        }
        return result;
    }

private:
    static constexpr std::size_t k_max_lines = 8;
    static constexpr std::size_t k_max_line_chars = 512;

    static LONG CALLBACK on_exception(EXCEPTION_POINTERS* info) noexcept {
        const EXCEPTION_RECORD& record = *info->ExceptionRecord;
        debugger_line_capture* const capture = active_.load(std::memory_order_acquire);
        if (record.ExceptionCode != DBG_PRINTEXCEPTION_WIDE_C || record.NumberParameters < 2 ||
            capture == nullptr || GetCurrentThreadId() != capture->thread_id_) {
            return EXCEPTION_CONTINUE_SEARCH;
        }
        const std::size_t index = capture->count_++;
        const auto* const text = reinterpret_cast<const wchar_t*>(record.ExceptionInformation[1]);
        const std::size_t with_terminator =
            static_cast<std::size_t>(record.ExceptionInformation[0]);
        if (index < k_max_lines && text != nullptr && with_terminator > 0) {
            const std::size_t length = std::min(with_terminator - 1, k_max_line_chars);
            std::copy_n(text, length, capture->text_[index].data());
            capture->length_[index] = length;
        }
        return EXCEPTION_CONTINUE_SEARCH;
    }

    static inline std::atomic<debugger_line_capture*> active_{nullptr};

    const DWORD thread_id_;
    PVOID cookie_ = nullptr;
    // Written only by the handler on thread_id_, which is also the only reader.
    std::size_t count_ = 0;
    std::array<std::array<wchar_t, k_max_line_chars>, k_max_lines> text_{};
    std::array<std::size_t, k_max_lines> length_{};
};

/**
 * A name no other event has, so a test can confirm that an event object was destroyed by opening
 * it by name, instead of probing a closed handle value the process may already have reused.
 */
std::wstring unique_event_name() {
    static std::atomic<std::uint32_t> next{0};
    return L"Local\\RAMQuick.engine.tests.CloseHandleChecked." +
           std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(next.fetch_add(1));
}

/**
 * Holds a test's event handle and lifts HANDLE_FLAG_PROTECT_FROM_CLOSE from it on every exit,
 * a fatal assertion's early return and an exception included.
 *
 * No owner can close a handle protected from close, so a test that protected one and then ended
 * early would leave it open for the rest of the process. The destructor therefore lifts the
 * protection, then closes an owned handle. A borrowed handle is closed by the owner it belongs
 * to: declared after that owner, this one is destroyed first and lifts the protection in time
 * for that close, and adopt() takes the handle over once that owner has given it up. Only a
 * close that succeeded gives the handle up, since after a failed one it is still open.
 */
class test_event_owner {
public:
    /** Whether the destructor closes the handle or leaves that to the owner it is borrowed from. */
    enum class ownership { owned, borrowed };

    /** Holds event, which may be null. */
    explicit test_event_owner(HANDLE event, ownership held = ownership::owned) noexcept
        : event_(event), owned_(held == ownership::owned) {}
    ~test_event_owner() {
        if (event_ == nullptr) {
            return;
        }
        // A failure to lift the protection is left to the close that follows, which reports it.
        SetHandleInformation(event_, HANDLE_FLAG_PROTECT_FROM_CLOSE, 0);
        if (owned_) {
            close_handle_checked(event_, L"test event");
        }
    }
    test_event_owner(const test_event_owner&) = delete;
    test_event_owner& operator=(const test_event_owner&) = delete;

    HANDLE get() const noexcept { return event_; }

    /** Protects the handle from close; false, with the reason in GetLastError(), on failure. */
    bool protect() noexcept {
        return SetHandleInformation(event_, HANDLE_FLAG_PROTECT_FROM_CLOSE,
                                    HANDLE_FLAG_PROTECT_FROM_CLOSE) != FALSE;
    }

    /** Lifts the protection; false, with the reason in GetLastError(), on failure. */
    bool unprotect() noexcept {
        return SetHandleInformation(event_, HANDLE_FLAG_PROTECT_FROM_CLOSE, 0) != FALSE;
    }

    /**
     * Takes over a borrowed handle that its owner gave up still open, and gives up one it closed.
     * The protection tells them apart: the owner's close of a protected handle is refused, and a
     * handle value the process reuses for a new handle comes without protection.
     */
    void adopt() noexcept {
        DWORD flags = 0;
        if (GetHandleInformation(event_, &flags) && (flags & HANDLE_FLAG_PROTECT_FROM_CLOSE) != 0) {
            owned_ = true;
        } else {
            relinquish();
        }
    }

    /** The close under test: close_handle_checked, giving the handle up only once it is closed. */
    DWORD close_checked(const wchar_t* what) noexcept {
        const DWORD error = close_handle_checked(event_, what);
        if (error == ERROR_SUCCESS) {
            relinquish();
        }
        return error;
    }

    /** Gives the handle up untouched, for a handle that is already closed. */
    void relinquish() noexcept {
        event_ = nullptr;
        owned_ = false;
    }

private:
    HANDLE event_;
    bool owned_;
};

static_assert(noexcept(std::declval<test_event_owner&>().~test_event_owner()),
              "test_event_owner cleans up while a failed assertion or an exception unwinds");

/** Creates an unsignalled manual-reset event under a new name, owned by the result. */
test_event_owner create_named_event(const std::wstring& name) {
    const HANDLE event = CreateEventW(nullptr, TRUE, FALSE, name.c_str());
    if (event == nullptr) {
        ADD_FAILURE() << "CreateEventW failed (Windows error " << GetLastError() << ")";
    }
    return test_event_owner{event};
}

/** Succeeds when no event object of this name exists any more: its last handle was closed. */
::testing::AssertionResult event_destroyed(const std::wstring& name) {
    const HANDLE reopened = OpenEventW(SYNCHRONIZE, FALSE, name.c_str());
    if (reopened != nullptr) {
        close_handle_checked(reopened, L"reopened test event");
        return ::testing::AssertionFailure() << "the event object still exists";
    }
    const DWORD error = GetLastError();
    if (error != ERROR_FILE_NOT_FOUND) {
        return ::testing::AssertionFailure()
               << "OpenEventW failed with Windows error " << error << ", not ERROR_FILE_NOT_FOUND";
    }
    return ::testing::AssertionSuccess();
}

/**
 * Under a debugger CloseHandle raises an exception for a handle it cannot close instead of
 * returning FALSE, and the debugger consumes debug output before any vectored handler sees it.
 */
constexpr char k_debugger_skip[] =
    "a debugger turns a refused CloseHandle into an exception and consumes debug output";

static_assert(noexcept(close_handle_checked(nullptr, nullptr)),
              "handle owners call close_handle_checked from destructors");

TEST(CloseHandleChecked, ClosesTheHandleAndWritesNothing) {
    debugger_line_capture capture;
    const std::wstring name = unique_event_name();
    test_event_owner event = create_named_event(name);
    ASSERT_NE(event.get(), nullptr);

    EXPECT_EQ(event.close_checked(L"test event"), static_cast<DWORD>(ERROR_SUCCESS));
    EXPECT_TRUE(event_destroyed(name));
    EXPECT_TRUE(capture.lines().empty());
}

TEST(CloseHandleChecked, StopSignalReportsAFailedCloseOfItsEvent) {
    if (IsDebuggerPresent()) {
        GTEST_SKIP() << k_debugger_skip;
    }
    debugger_line_capture capture;
    auto signal = std::make_unique<stop_signal>();
    // Borrowed while the signal owns the event and destroyed before it, so that after a failure
    // before reset() the signal's own close still succeeds.
    test_event_owner event{signal->event_handle(), test_event_owner::ownership::borrowed};
    ASSERT_TRUE(event.protect()) << "SetHandleInformation failed (Windows error " << GetLastError()
                                 << ")";

    // The destructor's close is refused; it reports that once and gives the event up, still
    // open, so the test owns it from here.
    signal.reset();
    event.adopt();
    EXPECT_EQ(capture.lines(),
              std::vector<std::wstring>{L"RAMQuick engine: CloseHandle of the session stop event "
                                        L"failed (Windows error 6)\n"});

    // The event is still open, so the test closes it; a second close would fail.
    ASSERT_TRUE(event.unprotect())
        << "SetHandleInformation failed (Windows error " << GetLastError() << ")";
    EXPECT_EQ(event.close_checked(L"test event"), static_cast<DWORD>(ERROR_SUCCESS));
    EXPECT_EQ(capture.lines().size(), 1U);
}

/** One description a failed close is reported with. */
struct close_failure_case {
    const char* name;
    /** The description passed to close_handle_checked; nullopt passes null. */
    std::optional<std::wstring> what;
    /** Whether the line is too long for the diagnostic and must be cut. */
    bool cut;
};

void PrintTo(const close_failure_case& value, std::ostream* os) {
    *os << value.name;
}

/**
 * Succeeds when lines holds exactly one line: full itself or, when cut, a shorter line that
 * begins as full does and still ends with its newline.
 */
::testing::AssertionResult reported_once(const std::vector<std::wstring>& lines,
                                         const std::wstring& full, bool cut) {
    if (lines.size() != 1U) {
        return ::testing::AssertionFailure() << lines.size() << " debugger lines, not 1";
    }
    const std::wstring& line = lines.front();
    const bool matches = cut ? !line.empty() && line.size() < full.size() && line.back() == L'\n' &&
                                   full.compare(0, line.size() - 1, line, 0, line.size() - 1) == 0
                             : line == full;
    if (!matches) {
        return ::testing::AssertionFailure() << "line \"" << line << "\" is not "
                                             << (cut ? "a cut of " : "") << "\"" << full << "\"";
    }
    return ::testing::AssertionSuccess();
}

class close_handle_checked_failure : public ::testing::TestWithParam<close_failure_case> {};

TEST_P(close_handle_checked_failure, ReportsOnceAndLeavesTheHandleOpen) {
    if (IsDebuggerPresent()) {
        GTEST_SKIP() << k_debugger_skip;
    }
    const close_failure_case& param = GetParam();
    const wchar_t* const what = param.what ? param.what->c_str() : nullptr;
    debugger_line_capture capture;
    const std::wstring name = unique_event_name();
    test_event_owner event = create_named_event(name);
    ASSERT_NE(event.get(), nullptr);
    // A handle protected from close makes CloseHandle fail while the handle stays valid.
    ASSERT_TRUE(event.protect()) << "SetHandleInformation failed (Windows error " << GetLastError()
                                 << ")";

    // Windows reports the refused close of a protected handle as ERROR_INVALID_HANDLE.
    EXPECT_EQ(event.close_checked(what), static_cast<DWORD>(ERROR_INVALID_HANDLE));

    const std::wstring full = L"RAMQuick engine: CloseHandle of the " +
                              (what != nullptr ? std::wstring(what) : std::wstring(L"handle")) +
                              L" failed (Windows error 6)\n";
    EXPECT_TRUE(reported_once(capture.lines(), full, param.cut));

    // Neither forced closed nor stripped of its protection: the event is still open and usable.
    DWORD flags = 0;
    EXPECT_TRUE(GetHandleInformation(event.get(), &flags))
        << "GetHandleInformation failed (Windows error " << GetLastError() << ")";
    EXPECT_EQ(flags & HANDLE_FLAG_PROTECT_FROM_CLOSE,
              static_cast<DWORD>(HANDLE_FLAG_PROTECT_FROM_CLOSE));
    EXPECT_EQ(WaitForSingleObject(event.get(), 0), static_cast<DWORD>(WAIT_TIMEOUT));

    // Once closable, one call closes it, the object goes away and nothing more is written.
    ASSERT_TRUE(event.unprotect())
        << "SetHandleInformation failed (Windows error " << GetLastError() << ")";
    EXPECT_EQ(event.close_checked(what), static_cast<DWORD>(ERROR_SUCCESS));
    EXPECT_TRUE(event_destroyed(name));
    EXPECT_EQ(capture.lines().size(), 1U);
}

/** Test-name suffix: the case's name, for example overlong. */
std::string failure_case_name(const ::testing::TestParamInfo<close_failure_case>& info) {
    return info.param.name;
}

const auto k_failure_cases =
    ::testing::Values(close_failure_case{"described", L"test event", false},
                      close_failure_case{"undescribed", std::nullopt, false},
                      close_failure_case{"overlong", std::wstring(400, L'w'), true});

INSTANTIATE_TEST_SUITE_P(Descriptions, close_handle_checked_failure, k_failure_cases,
                         failure_case_name);

}  // namespace
}  // namespace rq::engine::test
