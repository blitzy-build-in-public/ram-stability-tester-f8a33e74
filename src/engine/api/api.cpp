/**
 * @file api.cpp
 * @brief The exported functions of ramquick_engine.h and the boundary error contract.
 *
 * Each function makes only the argument checks that need no session, then one forwarding call
 * into rq::engine, and returns core's result code unchanged. Configuration validation, the deep
 * copy, the stop-reason record and the session registry live in core/session.cpp. Function
 * contracts are documented in ramquick_engine.h.
 *
 * ramquick_engine.dll, ramquick_engine_test.dll and any native test binary that calls the API
 * each compile this file, so it holds no DllMain and no static or global state: the only state
 * is core's, which every binary sees through the same session registry.
 *
 * Boundary error contract:
 * - Every result-returning function is noexcept and wraps its whole body, argument checks
 *   included, in try { ... } catch (...) { return RQ_E_INTERNAL; }, so no C++ exception
 *   (std::bad_alloc from a deep copy, std::system_error from an OS query) crosses the ABI.
 * - Under /EHsc, catch (...) catches C++ exceptions only. SEH faults (access violation, illegal
 *   instruction, in-page error) are deliberately neither caught nor translated, so no __try,
 *   SE translator or exception filter appears here: under unstable memory such a fault may
 *   itself be a symptom, and continuing would be unsafe. The process terminates, and the host
 *   reports the unfinished run at its next launch through journal recovery.
 * - The handlers are effective because every callee is a C++-linkage function in rq::engine:
 *   /EHc lets the compiler assume an extern "C" callee never throws and drop the handler around
 *   it, so these calls must never be routed through an extern "C" helper.
 */
#include "ramquick_engine.h"
#include "core/session.h"
#include "core/system_info.h"

extern "C" {

RQ_API uint32_t RQ_CALL rq_api_version(void) RQ_NOEXCEPT {
    return RQ_API_VERSION;
}

RQ_API rq_result RQ_CALL rq_query_system(rq_system_info* out) RQ_NOEXCEPT {
    try {
        // struct_size is checked before any other field is read or written.
        if (out == nullptr || out->struct_size != sizeof(rq_system_info)) {
            return RQ_E_INVALID_ARGUMENT;
        }
        return rq::engine::query_system(out);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

RQ_API rq_result RQ_CALL rq_session_create(const rq_session_config* config,
                                           const rq_callbacks* callbacks,
                                           rq_session_handle* out_session) RQ_NOEXCEPT {
    try {
        if (out_session == nullptr) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // Cleared before anything can fail, so no failure path, a caught exception included,
        // leaves a stale handle; core registers nothing and stores no handle unless it succeeds.
        *out_session = nullptr;
        // session_create runs validate_config on config and callbacks itself. A
        // default-constructed session_dependencies is the production set: the real
        // memory-access layer, qpc_clock and the Win32 thread affinity.
        return rq::engine::session_create(config, callbacks, rq::engine::session_dependencies{},
                                          out_session);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

RQ_API rq_result RQ_CALL rq_session_start(rq_session_handle session) RQ_NOEXCEPT {
    try {
        return rq::engine::session_start(session);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

RQ_API rq_result RQ_CALL rq_session_stop(rq_session_handle session,
                                         uint32_t stop_reason) RQ_NOEXCEPT {
    try {
        if (stop_reason != RQ_STOP_USER && stop_reason != RQ_STOP_THERMAL &&
            stop_reason != RQ_STOP_HOST_FAULT) {
            return RQ_E_INVALID_ARGUMENT;
        }
        // Core records the reason synchronously, before this call returns. The managed wrapper
        // sets its stop event only after the return, so a step gate woken by that event always
        // finds the reason recorded; deferring the record to another thread would break that.
        return rq::engine::session_stop(session, stop_reason);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

RQ_API rq_result RQ_CALL rq_session_get_result(rq_session_handle session,
                                               rq_run_result* out) RQ_NOEXCEPT {
    try {
        if (out == nullptr || out->struct_size != sizeof(rq_run_result)) {
            return RQ_E_INVALID_ARGUMENT;
        }
        return rq::engine::session_get_result(session, out);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

RQ_API rq_result RQ_CALL rq_session_destroy(rq_session_handle session) RQ_NOEXCEPT {
    try {
        // Core refuses a call from inside a callback with RQ_E_INVALID_STATE; otherwise no
        // callback of the session runs after it returns.
        return rq::engine::session_destroy(session);
    } catch (...) {
        return RQ_E_INTERNAL;
    }
}

RQ_API const char* RQ_CALL rq_result_message(rq_result code) RQ_NOEXCEPT {
    // Shown to users as "Engine error: <message>", so each text is short and has no final period.
    switch (code) {
    case RQ_OK:
        return "Success";
    case RQ_E_INVALID_ARGUMENT:
        return "Invalid argument";
    case RQ_E_INVALID_HANDLE:
        return "Invalid session handle";
    case RQ_E_INVALID_STATE:
        return "Operation not valid in the session's current state";
    case RQ_E_INSUFFICIENT_MEMORY:
        return "Not enough lockable memory for the minimum test region";
    case RQ_E_LOCK_FAILED:
        return "Memory could not be locked";
    case RQ_E_THREAD_CREATE:
        return "A test thread could not be created";
    case RQ_E_VERSION_MISMATCH:
        return "Engine API version mismatch";
    case RQ_E_AFFINITY_FAILED:
        return "Could not pin test threads to every logical processor";
    case RQ_E_INTERNAL:
        return "Internal engine error";
    default:
        return "Unknown result code";
    }
}

}  // extern "C"
