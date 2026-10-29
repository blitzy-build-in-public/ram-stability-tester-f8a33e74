/**
 * @file real_memory_access.cpp
 * @brief real_memory_access: each IMemoryAccess primitive forwarded to the kernel of the same
 *        role in the session's kernel_table.
 *
 * Compiled at the x64 SSE2 baseline and must never receive an /arch setting: the layer runs
 * before and regardless of ISA dispatch, so it holds no vector code of its own and reaches the
 * selected instruction set only through the table's function pointers. It issues no Win32 call.
 *
 * This unit defines the key function, the destructor, so the layer's vftable is emitted here
 * and nowhere else.
 */
#include "core/memory/real_memory_access.h"

#include <cassert>
#include <type_traits>

namespace rq::engine {

static_assert(!std::is_abstract_v<real_memory_access>,
              "real_memory_access must override every IMemoryAccess primitive");

real_memory_access::real_memory_access(const kernel_table& table) noexcept : kernels_(table) {
    // tables_for (core/kernels/isa_dispatch.h) always returns a complete table; a null entry
    // here would mean a kernel set was defined incompletely.
    assert(kernels_.fill_stream != nullptr);
    assert(kernels_.full_fence != nullptr);
    assert(kernels_.flush_range != nullptr);
    assert(kernels_.verify != nullptr);
    assert(kernels_.verify_and_stream != nullptr);
    assert(kernels_.reread != nullptr);
}

real_memory_access::~real_memory_access() = default;

// Each primitive is exactly one call to its kernel, with no fence or flush added. The step
// runner composes the flush-before-verification sequence (fence, flush, fence before every
// verification load) from these primitives itself, so every ordering step is a primitive that
// counting_memory_access logs when it wraps this layer, and test_step_runner.cpp can assert the
// rule for every verified line on every pass. Ordering added here would be invisible to that
// log. The kernels accept a zero-length range, so nothing is guarded here either.

void real_memory_access::stream_fill(const access_context& /*ctx*/, std::uint64_t* words,
                                     std::size_t word_count, const pattern_spec& spec,
                                     std::uint64_t first_word_index, int direction) {
    kernels_.fill_stream(words, word_count, spec, first_word_index, direction);
}

void real_memory_access::fence(const access_context& /*ctx*/) {
    kernels_.full_fence();
}

void real_memory_access::flush(const access_context& /*ctx*/, const std::uint64_t* words,
                               std::size_t word_count) {
    kernels_.flush_range(words, word_count * sizeof(std::uint64_t));
}

std::uint64_t real_memory_access::verify(const access_context& /*ctx*/, const std::uint64_t* words,
                                         std::size_t word_count, const pattern_spec& expected,
                                         std::uint64_t first_word_index, int direction,
                                         mismatch_sink& sink) {
    return kernels_.verify(words, word_count, expected, first_word_index, direction, sink);
}

std::uint64_t real_memory_access::verify_and_stream(const access_context& /*ctx*/,
                                                    std::uint64_t* words, std::size_t word_count,
                                                    const pattern_spec& expected,
                                                    const pattern_spec& next,
                                                    std::uint64_t first_word_index, int direction,
                                                    mismatch_sink& sink) {
    return kernels_.verify_and_stream(words, word_count, expected, next, first_word_index,
                                      direction, sink);
}

// The reread kernel already carries the whole re-read rule (fence, flush of the line, fence,
// one volatile load), so it is forwarded bare like the other primitives.
std::uint64_t real_memory_access::reread_word(const access_context& /*ctx*/,
                                              const std::uint64_t* word) noexcept {
    return kernels_.reread(word);
}

const kernel_table& real_memory_access::kernels() const noexcept {
    return kernels_;
}

}  // namespace rq::engine
