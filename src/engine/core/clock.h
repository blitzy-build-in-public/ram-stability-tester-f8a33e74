/**
 * @file clock.h
 * @brief The engine's clock seam (IClock and the QPC clock), the session stop signal, the
 *        UTF-8 truncation helper for fixed char[] payload fields and the checked CloseHandle
 *        that every engine handle owner closes through.
 *
 * Every engine timing reads the session's IClock, and every blocking wait that must honour a
 * stop waits through IClock::wait_for_stop on the session's stop_signal. The signal lives here
 * because the clock's interruptible wait needs it, and because it is the one object the
 * coordinator, the workers, memory preparation and rq_session_stop share.
 *
 * Includes <windows.h> and STL headers. It must therefore never be included by
 * core/kernels/kernels_avx2.cpp, core/kernels/kernels_avx512.cpp, core/kernels/kernels.h or
 * core/patterns/patterns.h: those are compiled (or included) at /arch:AVX2 and /arch:AVX512 and
 * may include only <immintrin.h>, <cstdint>, <cstddef>, kernels.h and patterns.h, so that no
 * inline function instantiated at a higher /arch level can be reached before ISA dispatch.
 */
#pragma once

#include <windows.h>
#include <atomic>
#include <cstdint>
#include <cstddef>
#include <cstring>
#include <cwchar>
#include <system_error>

#include "ramquick_engine.h"

namespace rq::engine {

/**
 * Copies a NUL-terminated UTF-8 string into a fixed char array, truncating at a UTF-8
 * character boundary.
 *
 * Used for rq_run_result.end_detail (through stop_signal::request), rq_system_info.cpu_name
 * (system_info.cpp) and step names (session.cpp), all of which the C API defines as UTF-8,
 * NUL-terminated within their fixed size, with longer text truncated at a character boundary.
 *
 * At most dst_size - 1 bytes of src are copied. When the cut would leave the first dropped byte
 * a continuation byte (10xxxxxx), the cut moves back to the lead byte of that sequence, so no
 * partial character remains. It moves back at most three bytes, the longest UTF-8 tail, so
 * malformed input never costs more than that. The rest of dst is zero-filled, so a payload field
 * carries no stale bytes across the boundary.
 *
 * @param dst Destination array; may be null only when dst_size is 0. Must not overlap src.
 * @param dst_size Size of dst in bytes, including the NUL terminator; 0 makes the call a no-op.
 * @param src NUL-terminated UTF-8 text; null is copied as the empty string.
 */
inline void copy_utf8_truncated(char* dst, size_t dst_size, const char* src) noexcept {
    if (dst == nullptr || dst_size == 0) {
        return;
    }
    size_t length = 0;
    if (src != nullptr) {
        const size_t limit = dst_size - 1;
        while (length < limit && src[length] != '\0') {
            ++length;
        }
        // src[length] is readable here: it is either the terminator or the first dropped byte.
        const size_t min_length = length > 3 ? length - 3 : 0;
        while (length > min_length && (static_cast<unsigned char>(src[length]) & 0xC0u) == 0x80u) {
            --length;
        }
        std::memcpy(dst, src, length);
    }
    std::memset(dst + length, 0, dst_size - length);
}

/**
 * Closes a kernel handle for an engine RAII owner and reports a failed close.
 *
 * Owners close in destructors, deleters and reset(), which must not throw, and the C API has no
 * warning channel. So a failed close is written as one line to the debugger, where the engine's
 * other non-fatal cleanup failures go, and returned for a caller that can act on it. The last
 * error is read straight after CloseHandle, before formatting can overwrite it, and an unset one
 * is reported as ERROR_GEN_FAILURE so that a failure never reads as success.
 *
 * The close is never retried, and the caller gives up the handle either way. A handle whose
 * close failed may be invalid, already closed or already reused for another object, so closing
 * the same value again could close a handle that other code owns.
 *
 * Never throws and never allocates: the line is formatted into a fixed stack buffer with
 * _snwprintf_s and _TRUNCATE, which cuts an overlong description instead of raising the CRT
 * invalid-parameter handler, which would terminate a static-CRT process. A cut line still ends
 * with its newline.
 *
 * @param handle The handle to close; the caller owns it and must not use it afterwards.
 * @param what Short English description of the handle for the diagnostic, for example
 *        L"session stop event"; null is reported as L"handle".
 * @return ERROR_SUCCESS when the handle was closed; otherwise the Windows error of the failed
 *         close, never 0.
 */
inline DWORD close_handle_checked(HANDLE handle, const wchar_t* what) noexcept {
    if (CloseHandle(handle)) {
        return ERROR_SUCCESS;
    }
    const DWORD last_error = GetLastError();
    const DWORD error = last_error != ERROR_SUCCESS ? last_error : ERROR_GEN_FAILURE;

    constexpr size_t k_line_capacity = 256;
    wchar_t line[k_line_capacity];
    // One element is held back so that the newline still fits after a cut.
    const int written =
        _snwprintf_s(line, k_line_capacity - 1, _TRUNCATE,
                     L"RAMQuick engine: CloseHandle of the %ls failed (Windows error %lu)",
                     what != nullptr ? what : L"handle", error);
    const size_t length = written >= 0 ? static_cast<size_t>(written) : std::wcslen(line);
    line[length] = L'\n';
    line[length + 1] = L'\0';
    OutputDebugStringW(line);
    return error;
}

/**
 * The single cancellation object of a session: an atomic stop flag, a manual-reset event and the
 * first-writer-wins end reason with its diagnostics.
 *
 * - The flag is a cheap hint for polling: workers check it after every 1 MiB block, and memory
 *   preparation checks it before every 256 MiB chunk.
 * - The event is for blocking waits: holds and every other stop-interruptible wait block on it
 *   through IClock::wait_for_stop, so a stop wakes them at once instead of after the wait.
 * - The end reason is first-writer-wins. The first of a host stop (rq_session_stop), the
 *   engine's own RQ_END_FIRST_ERROR (Stop on error), a fault (internal fault, step-gate abort,
 *   affinity failure, insufficient memory) and the coordinator's RQ_END_COMPLETED is kept,
 *   together with its os_error and detail, and it alone decides the terminal state. The
 *   coordinator records RQ_END_COMPLETED through request() once every step has run, which closes
 *   the race between "all steps finished" and a late stop or first error.
 *
 * Requesting a stop never waits. rq_session_stop is promised non-blocking, and the worker that
 * confirms the first mismatch must stop the run without waiting, so no requester takes a lock:
 * one that did could be held behind a descheduled holder, and a throwing lock acquisition would
 * terminate the process from a noexcept call. Instead the end reason doubles as the reservation.
 * A compare-exchange from 0 elects exactly one writer, which alone then writes os_error and
 * detail and publishes them through a ready flag. Only snapshot() may wait, and only for that
 * single fixed-size copy to finish.
 *
 * Every member is callable from any thread. The signal must outlive every thread that polls or
 * waits on it; the session owns it.
 */
class stop_signal {
public:
    /** The recorded end reason with its diagnostics, copied consistently by snapshot(). */
    struct end_info {
        /** RQ_END_* value, or 0 when no reason has been recorded. */
        uint32_t reason;
        /** Windows error code recorded with the reason; 0 when not applicable. */
        uint32_t os_error;
        /** UTF-8 diagnostic recorded with the reason, NUL-terminated and zero-filled. */
        char detail[RQ_END_DETAIL_SIZE];
    };

    /**
     * Creates the unsignalled manual-reset event.
     *
     * @throws std::system_error carrying the Windows error when CreateEventW fails. ABI entry
     *         points (api.cpp, test_exports.cpp) convert it to RQ_E_INTERNAL.
     */
    stop_signal() : event_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {
        if (event_ == nullptr) {
            throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                    "CreateEventW failed");
        }
    }

    /**
     * Closes the event. No thread may still be waiting on it. A failed close reaches the
     * debugger through close_handle_checked; a destructor can do nothing more about it.
     */
    ~stop_signal() { close_handle_checked(event_, L"session stop event"); }

    stop_signal(const stop_signal&) = delete;
    stop_signal& operator=(const stop_signal&) = delete;
    stop_signal(stop_signal&&) = delete;
    stop_signal& operator=(stop_signal&&) = delete;

    /**
     * Records an end reason if none has been recorded yet, then raises the flag and the event.
     *
     * The reason is claimed by a compare-exchange from 0, so exactly one call wins, and the
     * reason is visible through pending_reason() from the moment of the claim. A losing call has
     * read the earlier claim, so pending_reason() is nonzero once any valid call returns, winner
     * or not. The winner alone then writes os_error and detail and sets the ready flag that
     * snapshot() waits for, so snapshot() never pairs a reason with another call's diagnostics or
     * with a half-written detail. A losing call writes nothing.
     *
     * The flag and the event are raised by every valid call, and only after the claim: a thread
     * woken by the event, or one that runs after rq_session_stop has returned (the managed step
     * gate, which reads rq_test_pending_stop_reason in tests), therefore always finds the reason
     * in place.
     *
     * A failed SetEvent leaves the reason, the raised flag and the return value as they are; its
     * Windows error is kept in signal_error() for the coordinator to report. Waiters still see
     * the stop because qpc_clock::wait_for_stop rechecks the flag between bounded wait slices,
     * and every later valid call retries SetEvent.
     *
     * Takes no lock, never waits and allocates nothing, so it is safe from workers (first
     * error), the coordinator (faults, gate abort, completion) and host threads
     * (rq_session_stop) alike.
     *
     * @param end_reason An RQ_END_* value. 0 or a value above RQ_END_INTERNAL_FAULT is ignored:
     *        nothing is recorded or raised.
     * @param os_error Windows error code to record with the reason, or 0.
     * @param detail UTF-8 diagnostic, truncated to RQ_END_DETAIL_SIZE - 1 bytes; null records an
     *        empty detail.
     * @return true only for the call that recorded the reason.
     */
    bool request(uint32_t end_reason, uint32_t os_error = 0,
                 const char* detail = nullptr) noexcept {
        if (end_reason == 0 || end_reason > RQ_END_INTERNAL_FAULT) {
            return false;
        }
        uint32_t expected = 0;
        const bool recorded = reason_.compare_exchange_strong(
            expected, end_reason, std::memory_order_acq_rel, std::memory_order_acquire);
        if (recorded) {
            os_error_ = os_error;
            copy_utf8_truncated(detail_, sizeof(detail_), detail);
            details_ready_.store(true, std::memory_order_release);
        }
        stopped_.store(true, std::memory_order_release);
        if (!SetEvent(event_)) {
            const DWORD error = GetLastError();
            // A failure must never read as success, so an unset last error is reported as
            // ERROR_GEN_FAILURE. Only the first failure is kept, as the root one.
            uint32_t no_error = 0;
            signal_error_.compare_exchange_strong(
                no_error, static_cast<uint32_t>(error != 0 ? error : ERROR_GEN_FAILURE),
                std::memory_order_acq_rel, std::memory_order_acquire);
        }
        return recorded;
    }

    /** True once any valid request() has been made. Lock-free; polled every 1 MiB by workers. */
    bool stop_requested() const noexcept { return stopped_.load(std::memory_order_acquire); }

    /**
     * The RQ_END_* value recorded so far, or 0 when none has been. Lock-free. This is what
     * rq_test_pending_stop_reason reports.
     */
    uint32_t pending_reason() const noexcept { return reason_.load(std::memory_order_acquire); }

    /**
     * The manual-reset event, signalled by every valid request() and never reset. For
     * qpc_clock::wait_for_stop and any other wait that must wake on a stop; never close it.
     * The event stays unsignalled if every SetEvent failed (signal_error() != 0), so a wait on it
     * must also recheck stop_requested() at bounded intervals, as qpc_clock::wait_for_stop does.
     */
    HANDLE event_handle() const noexcept { return event_; }

    /**
     * The Windows error of the first SetEvent that failed in request(), or 0 while every one has
     * succeeded. Lock-free. The stop itself is unaffected; the coordinator reads this as a
     * diagnostic for the run result, because a signalling failure would otherwise leave no trace
     * beyond the added stop latency of a wait that had to recheck the flag.
     */
    uint32_t signal_error() const noexcept { return signal_error_.load(std::memory_order_acquire); }

    /**
     * Copies the reason with the os_error and detail its winning request() recorded, so the three
     * always belong to the same request; with no reason recorded, a zero end_info with an empty
     * detail. The coordinator fills rq_run_result.end_reason, os_error and end_detail from it.
     *
     * Between the winner's claim and its ready flag the reason is already visible but its
     * diagnostics are still being written, so snapshot() waits for the flag: a few pause spins,
     * then yields of the processor, then 1 ms sleeps. That wait is safe because in this window
     * the winner runs only one fixed-size copy, with no lock, allocation or system call, so only
     * its own descheduling can delay it, and the backoff hands it the processor. Only the
     * coordinator calls snapshot(), once the workers have joined; requesters and mismatch
     * workers never call it, so none of them ever waits.
     */
    end_info snapshot() const noexcept {
        end_info info{};
        info.reason = reason_.load(std::memory_order_acquire);
        if (info.reason == 0) {
            return info;
        }
        uint32_t attempts = 0;
        while (!details_ready_.load(std::memory_order_acquire)) {
            if (attempts < k_ready_spins) {
                YieldProcessor();
                ++attempts;
            } else if (attempts < k_ready_spins + k_ready_yields) {
                SwitchToThread();
                ++attempts;
            } else {
                Sleep(1);
            }
        }
        info.os_error = os_error_;
        std::memcpy(info.detail, detail_, sizeof(detail_));
        return info;
    }

private:
    // snapshot()'s backoff while the winner finishes its copy: pause spins cover the usual
    // nanoseconds-long window, yields cover a winner preempted on a busy processor, and 1 ms
    // sleeps keep a longer delay from costing the coordinator a busy core.
    static constexpr uint32_t k_ready_spins = 64;
    static constexpr uint32_t k_ready_yields = 16;

    // Polling hint for workers and memory preparation; raised after the claim.
    std::atomic<bool> stopped_{false};
    // The first-writer reservation and the recorded RQ_END_* value at once, so a reason is
    // visible the moment it is claimed: 0 until a valid request() claims it, never changed after.
    std::atomic<uint32_t> reason_{0};
    // Set (release) by the winner once os_error_ and detail_ are complete; snapshot() reads those
    // two only after observing it (acquire), which is the only ordering they need.
    std::atomic<bool> details_ready_{false};
    // Written once, by the winner, between its claim and details_ready_.
    uint32_t os_error_ = 0;
    char detail_[RQ_END_DETAIL_SIZE] = {};
    // First SetEvent failure, kept separate from os_error_ so it never displaces the diagnostics
    // of the end reason itself.
    std::atomic<uint32_t> signal_error_{0};
    HANDLE event_ = nullptr;
};

/**
 * The engine's clock seam.
 *
 * Every engine timing reads now_ns() from the session's IClock: pass durations, step budgets
 * and overrun, the progress cadence and time remaining, time_into_test_ms, time_since_write_us,
 * first_error_ms, prepare_ms, release_ms and error-batch spacing. Hold delays and other
 * stop-interruptible waits call wait_for_stop() instead of sleeping, so the substitute
 * tests/engine.tests/fakes/fake_clock.h can complete a 5 s hold instantly by advancing fake time
 * while still honouring a stop (it returns true at once when signal.stop_requested()).
 *
 * The PascalCase, I-prefixed name departs from the engine's snake_case convention because the
 * plan names the seam IClock, alongside IMemoryAccess.
 */
class IClock {
public:
    virtual ~IClock() = default;

    /** Monotonic nanoseconds since an arbitrary origin. Callable from any thread. */
    virtual uint64_t now_ns() const noexcept = 0;

    /**
     * Waits up to timeout_ms or until the signal is stopped, whichever comes first. A stop must
     * end the wait even when its event could not be signalled (stop_signal::signal_error), so an
     * implementation observes stop_requested() rather than relying on the event alone.
     *
     * @return true when the signal is stopped; false when the timeout elapsed without a stop.
     * @throws std::system_error when the wait itself fails. The coordinator and worker entry
     *         points convert it to RQ_END_INTERNAL_FAULT.
     */
    virtual bool wait_for_stop(const stop_signal& signal, uint64_t timeout_ms) = 0;
};

/** The production clock: QueryPerformanceCounter, and waits on the stop signal's event. */
class qpc_clock final : public IClock {
public:
    /** Reads the counter frequency once; it is fixed at boot and the call cannot fail. */
    qpc_clock() noexcept {
        LARGE_INTEGER frequency{};
        QueryPerformanceFrequency(&frequency);
        frequency_ = static_cast<uint64_t>(frequency.QuadPart);
    }

    /**
     * The performance counter in nanoseconds. Whole seconds and the remainder are converted
     * separately: count * 10^9 would overflow 64 bits after about 30 minutes of uptime at the
     * 10 MHz counter of Windows 10 and later, while rem * 10^9 stays in range for any counter
     * frequency below 18 GHz.
     */
    uint64_t now_ns() const noexcept override {
        LARGE_INTEGER counter{};
        QueryPerformanceCounter(&counter);
        const uint64_t count = static_cast<uint64_t>(counter.QuadPart);
        const uint64_t whole = count / frequency_;
        const uint64_t rem = count % frequency_;
        return whole * k_ns_per_second + rem * k_ns_per_second / frequency_;
    }

    /**
     * Blocks on the stop event rather than sleeping, so a stop wakes a 5, 30 or 120 s hold at
     * once and the run still reaches Completed within 2 s of the request.
     *
     * The timeout is waited in slices of at most k_stop_recheck_ms, and the flag is rechecked
     * after each one, because the event stays unsignalled when SetEvent failed in
     * stop_signal::request (stop_signal::signal_error). Such a stop then ends the wait within one
     * slice instead of at the timeout.
     *
     * The wait ends at a deadline fixed on now_ns() when it starts, and each slice waits only
     * what is left of it. WaitForSingleObject rounds every timeout up to the system timer tick
     * (about 15.6 ms by default), so counting slices by their nominal length would make each
     * 100 ms slice cost about 109 ms and stretch a 120 s hold by about 10 s. Measured against the
     * deadline, the wait overruns by at most about one tick, whatever its length. What is left is
     * rounded up to whole milliseconds, so a sub-millisecond remainder blocks for 1 ms instead of
     * spinning on zero-length polls. A timeout of 0 is a single poll; INFINITE is never passed.
     */
    bool wait_for_stop(const stop_signal& signal, uint64_t timeout_ms) override {
        if (signal.stop_requested()) {
            return true;
        }
        uint64_t now = now_ns();
        // A timeout beyond the nanosecond range (centuries) saturates the deadline, so the wait
        // then runs until a stop instead of wrapping to an early deadline.
        const uint64_t deadline_ns = timeout_ms <= (UINT64_MAX - now) / k_ns_per_ms
                                         ? now + timeout_ms * k_ns_per_ms
                                         : UINT64_MAX;
        for (;;) {
            const uint64_t left_ns = deadline_ns - now;
            const uint64_t left_ms = left_ns / k_ns_per_ms + (left_ns % k_ns_per_ms != 0 ? 1 : 0);
            const DWORD slice_ms =
                static_cast<DWORD>(left_ms < k_stop_recheck_ms ? left_ms : k_stop_recheck_ms);
            const DWORD rc = WaitForSingleObject(signal.event_handle(), slice_ms);
            if (rc == WAIT_OBJECT_0) {
                return true;
            }
            if (rc != WAIT_TIMEOUT) {
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                        "WaitForSingleObject on the stop event failed");
            }
            // The flag is raised before the event is set, so this also catches a stop that races
            // the slice's timeout, not only one whose event was never signalled.
            if (signal.stop_requested()) {
                return true;
            }
            now = now_ns();
            if (now >= deadline_ns) {
                return false;
            }
        }
    }

private:
    static constexpr uint64_t k_ns_per_second = 1'000'000'000;
    static constexpr uint64_t k_ns_per_ms = 1'000'000;
    // Bounds the extra stop latency when the stop event could not be signalled: 100 ms is well
    // within the 2 s stop bound and costs each waiter only ten wakeups per second.
    static constexpr DWORD k_stop_recheck_ms = 100;

    uint64_t frequency_ = 1;
};

}  // namespace rq::engine
