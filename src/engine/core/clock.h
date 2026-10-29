/**
 * @file clock.h
 * @brief The engine's clock seam (IClock and the QPC clock), the session stop signal and the
 *        UTF-8 truncation helper for fixed char[] payload fields.
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
#include <mutex>
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

    /** Closes the event. No thread may still be waiting on it. */
    ~stop_signal() { CloseHandle(event_); }

    stop_signal(const stop_signal&) = delete;
    stop_signal& operator=(const stop_signal&) = delete;
    stop_signal(stop_signal&&) = delete;
    stop_signal& operator=(stop_signal&&) = delete;

    /**
     * Records an end reason if none has been recorded yet, then raises the flag and the event.
     *
     * The reason, os_error and detail are written together under the lock, so snapshot() never
     * sees a reason without its detail. The flag and the event are raised by every valid call,
     * first writer or not, and only after the reason is recorded: a thread woken by the event,
     * or one that runs after rq_session_stop has returned (the managed step gate, which reads
     * rq_test_pending_stop_reason in tests), therefore always finds the reason in place.
     *
     * Allocates nothing; called from workers (first error), the coordinator (faults, gate abort,
     * completion) and host threads (rq_session_stop).
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
        bool recorded = false;
        {
            std::lock_guard<std::mutex> lock(detail_mutex_);
            if (reason_.load(std::memory_order_relaxed) == 0) {
                os_error_ = os_error;
                copy_utf8_truncated(detail_, sizeof(detail_), detail);
                reason_.store(end_reason, std::memory_order_release);
                recorded = true;
            }
        }
        stopped_.store(true, std::memory_order_release);
        SetEvent(event_);
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
     * The manual-reset event, signalled by the first valid request() and never reset. For
     * qpc_clock::wait_for_stop and any other wait that must wake on a stop; never close it.
     */
    HANDLE event_handle() const noexcept { return event_; }

    /**
     * Copies the reason, os_error and detail under the lock, so the three always belong to the
     * same request. The coordinator fills rq_run_result.end_reason, os_error and end_detail from
     * it.
     */
    end_info snapshot() const noexcept {
        end_info info{};
        std::lock_guard<std::mutex> lock(detail_mutex_);
        info.reason = reason_.load(std::memory_order_relaxed);
        info.os_error = os_error_;
        std::memcpy(info.detail, detail_, sizeof(detail_));
        return info;
    }

private:
    std::atomic<bool> stopped_{false};
    std::atomic<uint32_t> reason_{0};
    // Written only by the first writer, under detail_mutex_.
    uint32_t os_error_ = 0;
    char detail_[RQ_END_DETAIL_SIZE] = {};
    mutable std::mutex detail_mutex_;
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
     * Waits up to timeout_ms or until the signal is stopped, whichever comes first.
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
     * once and the run still reaches Completed within 2 s of the request. Timeouts beyond one
     * WaitForSingleObject call are waited in slices; INFINITE is never passed.
     */
    bool wait_for_stop(const stop_signal& signal, uint64_t timeout_ms) override {
        if (signal.stop_requested()) {
            return true;
        }
        uint64_t left_ms = timeout_ms;
        do {
            const DWORD slice_ms =
                static_cast<DWORD>(left_ms < k_max_wait_slice_ms ? left_ms : k_max_wait_slice_ms);
            const DWORD rc = WaitForSingleObject(signal.event_handle(), slice_ms);
            if (rc == WAIT_OBJECT_0) {
                return true;
            }
            if (rc != WAIT_TIMEOUT) {
                throw std::system_error(static_cast<int>(GetLastError()), std::system_category(),
                                        "WaitForSingleObject on the stop event failed");
            }
            left_ms -= slice_ms;
        } while (left_ms > 0);
        // The flag is raised before the event is set, so a stop racing the timeout is seen here.
        return signal.stop_requested();
    }

private:
    static constexpr uint64_t k_ns_per_second = 1'000'000'000;
    static constexpr DWORD k_max_wait_slice_ms = INFINITE - 1;

    uint64_t frequency_ = 1;
};

}  // namespace rq::engine
