/**
 * @file patterns.cpp
 * @brief Out-of-line scalar reference of the data patterns.
 *
 * This unit is compiled at the x64 SSE2 baseline and must never receive an /arch setting.
 */
#include "core/patterns/patterns.h"

namespace rq::engine {

/*
 * expected_value is the ground truth that the SSE2, AVX2 and AVX-512 generators must match bit
 * for bit, so it is defined here, out of line, rather than inline in patterns.h:
 * - baseline code runs on every x64 CPU, so the AVX kernel units, the test fakes and the tests
 *   can all call it, whichever kernel set dispatch selected;
 * - an inline definition of this untagged function would become a COMDAT in every unit that
 *   compiles it, the AVX units included, and the linker could keep an AVX copy for baseline
 *   callers (the hazard described in patterns.h). Its instantiation of pattern_word carries the
 *   isa_scalar tag, so that symbol cannot collide with a vector unit's either.
 */
std::uint64_t expected_value(const pattern_spec& spec, std::uint64_t word_index) noexcept {
    return pattern_word<isa_scalar>(spec, word_index);
}

}  // namespace rq::engine
