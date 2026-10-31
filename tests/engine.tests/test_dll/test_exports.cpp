/**
 * @file test_exports.cpp
 * @brief The six rq_test_* exports of ramquick_engine_test.dll.
 *
 * Compiled only into ramquick_engine_test.dll, next to the unchanged src/engine/api/api.cpp that
 * supplies the production exports; never into ramquick_engine.dll or engine.tests.exe. Function
 * contracts are documented in test_dll/ramquick_engine_test.h.
 *
 * Configuration validation, the deep copy, the session registry, the stop-reason record and the
 * live-session count all belong to core/session.cpp, which both DLLs share. This file only
 * assembles the substitute session_dependencies for rq_test_session_create and reads the
 * substitute layers' accessors, so a test session differs from a production one in its seams
 * alone. It holds no static or global state of its own.
 *
 * Boundary error contract, as in api.cpp:
 * - Every result-returning export is noexcept and wraps its whole body in
 *   try { ... } catch (...) { return RQ_E_INTERNAL; }, so no C++ exception crosses the ABI.
 * - Under /EHsc, catch (...) catches C++ exceptions only. SEH faults are deliberately neither
 *   caught nor translated: no __try, SE translator or exception filter appears here.
 * - Every callee is a C++-linkage function, because /EHc lets the compiler assume an extern "C"
 *   callee never throws and drop the handler around it.
 */
#include "ramquick_engine.h"
#include "test_dll/ramquick_engine_test.h"
#include "core/session.h"
#include "core/clock.h"
#include "core/runner/worker_pool.h"
#include "core/memory/memory_access.h"
#include "fakes/fault_spec.h"
#include "fakes/counting_memory_access.h"
#include "fakes/fault_injecting_memory_access.h"

#include <cstdint>
#include <memory>
#include <algorithm>
#include <utility>
#include <vector>

namespace {

/**
 * Affinity decorator behind rq_fault_spec.affinity_fail_worker: the named worker's pin reports
 * fail_error without pinning, and every other worker is pinned by the real affinity. The core
 * turns the failure into RQ_END_AFFINITY_FAILED with os_error = fail_error and the detail
 * "SetThreadGroupAffinity failed: group g, processor p", which is how the managed tests observe
 * an affinity failure the host machine never produces on its own.
 *
 * The worker pool calls pin() concurrently from every worker thread. The members are set at
 * construction and never modified, and the real affinity is stateless, so no synchronisation is
 * needed.
 */
class failing_thread_affinity final : public rq::engine::thread_affinity {
public:
    failing_thread_affinity(std::shared_ptr<rq::engine::thread_affinity> real,
                            std::uint32_t fail_worker, std::uint32_t fail_error) noexcept
        : real_(std::move(real)), fail_worker_(fail_worker), fail_error_(fail_error) {}

    std::uint32_t pin(std::uint32_t worker_index, std::uint16_t group,
                      std::uint8_t processor) override {
        if (worker_index == fail_worker_) {
            return fail_error_;
        }
        return real_->pin(worker_index, group, processor);
    }

private:
    std::shared_ptr<rq::engine::thread_affinity> real_;
    std::uint32_t fail_worker_;
    std::uint32_t fail_error_;
};

/**
 * The fault-injecting layer of a session made by rq_test_session_create, which always installs
 * it outermost; null for any other session, whose outermost layer is the real one.
 */
rq::engine::test::fault_injecting_memory_access* fault_layer_of(rq::engine::session& s) noexcept {
    return dynamic_cast<rq::engine::test::fault_injecting_memory_access*>(&s.memory_access());
}

/** The counting layer directly beneath a test session's fault-injecting layer, or null. */
rq::engine::test::counting_memory_access*
counting_layer_of(rq::engine::test::fault_injecting_memory_access& fault) noexcept {
    return dynamic_cast<rq::engine::test::counting_memory_access*>(fault.inner());
}

/**
 * Reports why rq_test_session_create rejected a fault specification to an attached debugger.
 * The ABI has only RQ_E_INVALID_ARGUMENT for every rule, so this is the one place that names it.
 */
void report_rejected_spec(const char* reason) noexcept {
    OutputDebugStringA("rq_test_session_create rejected the fault specification: ");
    OutputDebugStringA(reason != nullptr ? reason : "unknown rule");
    OutputDebugStringA("\n");
}

}  // namespace

extern "C" RQ_API rq_result RQ_CALL rq_test_session_create(const rq_session_config* config,
                                                           const rq_callbacks* callbacks,
                                                           const rq_fault_spec* fault,
                                                           rq_session_handle* out) RQ_NOEXCEPT {
    try {
        if (out == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // Cleared before anything can fail, so no failure path leaves a stale handle.
        *out = nullptr;

        // The spec checks below read config->region_bytes and config->worker_count. Core's own
        // validation runs first, so a null config or callback set, or an undersized struct, is
        // rejected before any of its fields is read; session_create repeats it as for every
        // session.
        const rq_result validation = rq::engine::validate_config(config, callbacks);
        if (validation != RQ_OK) {
            return validation;
        }

        rq_fault_spec requested = rq::engine::test::make_fault_spec(RQ_FAULT_NONE);
        if (fault != nullptr) {
            // validate_fault_spec checks struct_size before any other field and stops at the
            // first rule broken, so an undersized spec is never read beyond it; the spec is
            // copied only once it is known to be whole. It also requires a nonzero
            // affinity_fail_error whenever affinity_fail_worker names a worker.
            const char* reason = nullptr;
            if (!rq::engine::test::validate_fault_spec(*fault, config->region_bytes, &reason)) {
                report_rejected_spec(reason);
                return RQ_E_INVALID_ARGUMENT;
            }
            requested = *fault;
        }
        // The one rule validate_fault_spec cannot check, because it does not know the worker
        // count: a worker index the session never creates would silently fail nothing.
        if (requested.affinity_fail_worker != RQ_FAULT_NO_WORKER &&
            requested.affinity_fail_worker >= config->worker_count) {
            report_rejected_spec("affinity_fail_worker must be below worker_count");
            return RQ_E_INVALID_ARGUMENT;
        }
        const rq_fault_spec spec = rq::engine::test::normalized(requested, config->region_bytes);

        // Duplicated before anything is built, so a handle the caller closes on return stays
        // valid for the session, and a bad one is rejected before any allocation. The owner
        // closes the duplicate on every failure path below.
        rq::engine::test::hold_event_handle hold_entered;
        if (spec.hold_entered_event != 0) {
            const HANDLE process = GetCurrentProcess();
            const auto caller_event =
                reinterpret_cast<HANDLE>(static_cast<std::uintptr_t>(spec.hold_entered_event));
            HANDLE duplicate = nullptr;
            if (!DuplicateHandle(process, caller_event, process, &duplicate, 0, FALSE,
                                 DUPLICATE_SAME_ACCESS)) {
                report_rejected_spec("hold_entered_event is not a handle this process can "
                                     "duplicate");
                return RQ_E_INVALID_ARGUMENT;
            }
            hold_entered.reset(duplicate);
        }
        // validate_config has checked step_count and that steps holds step_count elements.
        std::vector<std::uint32_t> hold_ms_by_step(config->step_count);
        for (std::uint32_t i = 0; i < config->step_count; ++i) {
            hold_ms_by_step[i] = config->steps[i].hold_ms;
        }

        rq::engine::session_dependencies dependencies;
        // One clock instance serves both the session and the fault layer: delay-dependent
        // injection measures access_context::write_time_ns, which the runner stamps from the
        // session clock, against that clock's readings, so two clocks would not share a time
        // base. The hold clock reads QueryPerformanceCounter for every engine timing and the hold
        // waits, and measures the delay-dependent age on its controlled timeline instead, which
        // only hold entries advance. The session owns it for its lifetime and destroys its
        // memory-access stack first.
        auto clock = std::make_shared<rq::engine::test::hold_clock>(
            std::make_shared<rq::engine::qpc_clock>(), config->worker_count,
            std::move(hold_ms_by_step), std::move(hold_entered));
        dependencies.clock = clock;
        // The full stack is built for RQ_FAULT_NONE too, where the fault layer only passes
        // accesses through and counts pre-hold fences, so the layer queries below find the same
        // stack on every test session. The counting layer keeps its default options: no
        // primitive log and no clock, so DLL sessions carry no logging memory.
        using rq::engine::IMemoryAccess;
        using rq::engine::test::counting_memory_access;
        using rq::engine::test::fault_injecting_memory_access;
        dependencies.wrap_memory_access =
            [spec, clock](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
            return std::make_unique<fault_injecting_memory_access>(
                std::make_unique<counting_memory_access>(std::move(real)), spec, *clock,
                clock.get());
        };
        // Left null otherwise, which selects the production Win32 affinity.
        if (spec.affinity_fail_worker != RQ_FAULT_NO_WORKER) {
            dependencies.affinity = std::make_shared<failing_thread_affinity>(
                rq::engine::make_win32_thread_affinity(), spec.affinity_fail_worker,
                spec.affinity_fail_error);
        }

        // The deep copy, ISA dispatch, construction of the stack and registration belong to
        // core; the session takes ownership of the dependencies.
        return rq::engine::session_create(config, callbacks, std::move(dependencies), out);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

extern "C" RQ_API rq_result RQ_CALL
rq_test_get_independent_verified_bytes(rq_session_handle handle, uint64_t* out) RQ_NOEXCEPT {
    try {
        if (out == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // Held for the whole query, so a concurrent rq_session_destroy cannot free the layers
        // while they are read.
        const std::shared_ptr<rq::engine::session> target = rq::engine::acquire_session(handle);
        if (!target) {
            return RQ_E_INVALID_HANDLE;
        }
        rq::engine::test::fault_injecting_memory_access* const fault = fault_layer_of(*target);
        if (fault == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        const rq::engine::test::counting_memory_access* const counting = counting_layer_of(*fault);
        if (counting == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // An atomic total, so it is safe to read while workers are still verifying.
        *out = counting->verified_bytes();
        return RQ_OK;
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

extern "C" RQ_API rq_result RQ_CALL rq_test_get_injection_log(rq_session_handle handle,
                                                              rq_injected_fault* buffer,
                                                              uint32_t cap,
                                                              uint32_t* count) RQ_NOEXCEPT {
    try {
        if (count == nullptr || (cap > 0 && buffer == nullptr)) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // Held for the whole query, as in rq_test_get_independent_verified_bytes.
        const std::shared_ptr<rq::engine::session> target = rq::engine::acquire_session(handle);
        if (!target) {
            return RQ_E_INVALID_HANDLE;
        }
        const rq::engine::test::fault_injecting_memory_access* const fault =
            fault_layer_of(*target);
        if (fault == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // A snapshot copied under the layer's log mutex, in the order the entries were logged.
        const auto log = fault->injection_log();
        const std::size_t copied = std::min<std::size_t>(cap, log.size());
        if (copied != 0) {
            std::copy_n(log.begin(), copied, buffer);
        }
        // The total is written whatever cap is, so a caller can size its buffer with cap 0 first.
        *count = static_cast<uint32_t>(std::min<std::size_t>(log.size(), UINT32_MAX));
        return RQ_OK;
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

extern "C" RQ_API uint32_t RQ_CALL rq_test_live_session_count(void) RQ_NOEXCEPT {
    // Core's lock-free count of every session in this DLL not yet destroyed, created through
    // rq_session_create and rq_test_session_create alike.
    return rq::engine::live_session_count();
}

extern "C" RQ_API rq_result RQ_CALL rq_test_pending_stop_reason(rq_session_handle handle,
                                                                uint32_t* out) RQ_NOEXCEPT {
    try {
        if (out == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        const std::shared_ptr<rq::engine::session> target = rq::engine::acquire_session(handle);
        if (!target) {
            return RQ_E_INVALID_HANDLE;
        }
        // The first-writer-wins RQ_END_* value, readable the moment rq_session_stop has claimed
        // it, so a step gate woken by the managed stop event always finds it here.
        *out = target->pending_stop_reason();
        return RQ_OK;
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

extern "C" RQ_API rq_result RQ_CALL rq_test_struct_sizes(rq_struct_sizes* out) RQ_NOEXCEPT {
    try {
        // struct_size is checked before any other field is written.
        if (out == nullptr || out->struct_size != sizeof(rq_struct_sizes)) {
            return RQ_E_INVALID_ARGUMENT;
        }
        out->system_info = static_cast<uint32_t>(sizeof(rq_system_info));
        out->step_def = static_cast<uint32_t>(sizeof(rq_step_def));
        out->session_config = static_cast<uint32_t>(sizeof(rq_session_config));
        out->callbacks = static_cast<uint32_t>(sizeof(rq_callbacks));
        out->prepared = static_cast<uint32_t>(sizeof(rq_prepared));
        out->step_start = static_cast<uint32_t>(sizeof(rq_step_start));
        out->step_result = static_cast<uint32_t>(sizeof(rq_step_result));
        out->progress = static_cast<uint32_t>(sizeof(rq_progress));
        out->error_record = static_cast<uint32_t>(sizeof(rq_error_record));
        out->run_result = static_cast<uint32_t>(sizeof(rq_run_result));
        out->fault_spec = static_cast<uint32_t>(sizeof(rq_fault_spec));
        out->injected_fault = static_cast<uint32_t>(sizeof(rq_injected_fault));
        out->reserved0 = 0;
        return RQ_OK;
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}
