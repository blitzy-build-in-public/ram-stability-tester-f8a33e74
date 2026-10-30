/**
 * @file test_error_collector.cpp
 * @brief Error accounting tests: the stored-detail cap, exact true totals and per-step counts,
 *        on_errors batching, first_error_ms and Stop on error.
 *
 * - ErrorCollectorConstruction: rejected options and the state of a fresh collector.
 * - ErrorCollectorCap: eight workers racing 16,000 mismatches past the 10,000-record cap while
 *   the coordinator drains, and a cap of 0. Totals stay exact, the first cap records in
 *   detection order are stored exactly once, and every mismatch is re-read exactly once.
 * - ErrorCollectorBatch: what each drain() delivers before, at and after the cap.
 * - ErrorCollectorStepCounts: per-step true counts across step boundaries, past the cap.
 * - ErrorCollectorFirstError: first_error_ms as test time of the earliest error.
 * - ErrorCollectorStopOnError: the first confirming worker requests RQ_END_FIRST_ERROR itself,
 *   on its own thread, before on_mismatch returns and without any drain or callback; racing
 *   workers record it once, an earlier stop keeps its reason, and Stop on error off requests
 *   nothing.
 * - ErrorCollectorFirstErrorDetail: the exact text of that stop's end detail, which numbers the
 *   step and pass from 1, as the results log does, and the worker from 0, and names the word.
 * - ErrorCollectorRecord: every field of a stored rq_error_record.
 * - ErrorCollectorSession: a real 2-worker, 256 MiB session over the fault-injecting ->
 *   counting -> real stack, whose on_errors batches arrive at most every 100 ms, on the
 *   coordinator only, with an exact, non-decreasing total and at most the capped detail. A host
 *   stop mid-step may bring in early only the run's final batch, which still precedes
 *   Completed with every stored record and the true total, within 2 s of the stop.
 *
 * The collector tests drive worker_error_sink directly from plain std::threads over one
 * committed, unlocked 64 MiB VirtualAlloc region, so each re-read loads real memory through
 * real_memory_access. Each reported word is written once and re-read once: no
 * flush-and-reload loop over a small address set, the access style the stress bound excludes.
 * The session test locks 256 MiB, which needs no elevation, and pins two workers.
 */
#include <gtest/gtest.h>
#include <windows.h>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <latch>
#include <memory>
#include <mutex>
#include <ostream>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "ramquick_engine.h"
#include "core/clock.h"
#include "core/system_info.h"
#include "core/kernels/isa_dispatch.h"
#include "core/memory/memory_access.h"
#include "core/memory/real_memory_access.h"
#include "core/errors/error_collector.h"
#include "core/session.h"
#include "core/runner/step_runner.h"
#include "fakes/fake_clock.h"
#include "fakes/counting_memory_access.h"
#include "fakes/fault_injecting_memory_access.h"
#include "fakes/fault_spec.h"

namespace rq::engine::test {
namespace {

/* ---------------------------------------------------------------------------------------------
 * Constants
 * ------------------------------------------------------------------------------------------- */

/** Bytes of the region the collector tests report mismatches in. */
constexpr std::size_t k_region_bytes = std::size_t{64} << 20U;
constexpr std::size_t k_region_words = k_region_bytes / sizeof(std::uint64_t);

/** Detail cap the application passes. */
constexpr std::uint32_t k_detail_cap = 10'000U;

/** Concurrent workers of the race tests, and the most host processors their placements use. */
constexpr std::uint32_t k_race_workers = 8U;

/** Mismatches each racing worker reports in the cap test: 16,000 in all, past the cap. */
constexpr std::uint32_t k_errors_per_worker = 2'000U;
constexpr std::uint64_t k_race_total = std::uint64_t{k_race_workers} * k_errors_per_worker;

/** Spacing of the cap test's words, so its 16,000 words spread over the whole region. */
constexpr std::size_t k_race_stride_words = (k_region_words / k_race_total) & ~std::size_t{7};

static_assert(k_race_total > k_detail_cap, "the cap test must exceed the cap");
static_assert(k_race_stride_words >= 8U, "each racing word must fall on its own cache line");

constexpr std::uint64_t k_ns_per_us = 1'000U;
constexpr std::uint64_t k_ns_per_ms = 1'000'000U;

/**
 * How far the record test's wrapping chunk's logical offset lies above the chunk's own address,
 * so that its logical base, address minus logical offset, lies below zero and wraps.
 */
constexpr std::uint64_t k_wrap_margin = std::uint64_t{96} << 20U;

/* ---------------------------------------------------------------------------------------------
 * Names and values
 * ------------------------------------------------------------------------------------------- */

/** 0x followed by 16 uppercase hex digits, as the summary and the UI show words. */
std::string hex64(std::uint64_t value) {
    static constexpr char digits[] = "0123456789ABCDEF";
    std::string text = "0x0000000000000000";
    for (std::size_t i = text.size(); i-- > 2U;) {
        text[i] = digits[value & 0xFU];
        value >>= 4U;
    }
    return text;
}

/** Virtual address of a word, as rq_error_record.address carries it. */
std::uint64_t address_of(const std::uint64_t* word) noexcept {
    return static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(word));
}

/** The expected value of the n-th reported word: distinct and nonzero for every n. */
constexpr std::uint64_t expected_for(std::uint64_t n) noexcept {
    return (n + 1U) * 0x9E3779B97F4A7C15ULL;
}

/** The value the n-th reported word holds instead: one bit, rotating with n, flipped. */
constexpr std::uint64_t actual_for(std::uint64_t n) noexcept {
    return expected_for(n) ^ (std::uint64_t{1} << (n % 64U));
}

/* ---------------------------------------------------------------------------------------------
 * Region, kernels, placements and contexts
 * ------------------------------------------------------------------------------------------- */

/** Releases a VirtualAlloc region. */
struct virtual_free_deleter {
    void operator()(std::uint64_t* words) const noexcept { VirtualFree(words, 0, MEM_RELEASE); }
};

/**
 * k_region_bytes of committed, unlocked, zeroed read-write memory, released on destruction. Its
 * base serves as access_context::region_base, so a word's logical offset is its byte distance
 * from the base.
 */
class region_buffer {
public:
    region_buffer()
        : words_(static_cast<std::uint64_t*>(
              VirtualAlloc(nullptr, k_region_bytes, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE))) {}

    /** False when VirtualAlloc failed. */
    bool valid() const noexcept { return words_ != nullptr; }
    /** The word at index, below k_region_words. */
    std::uint64_t* word(std::size_t index) noexcept { return words_.get() + index; }
    std::uintptr_t base() const noexcept { return reinterpret_cast<std::uintptr_t>(words_.get()); }

private:
    std::unique_ptr<std::uint64_t, virtual_free_deleter> words_;
};

/** The table a session on this host uses: the automatic selection, patched by tables_for. */
kernel_table host_kernels() noexcept {
    return tables_for(select_isa(read_cpu_features(), RQ_ISA_AUTO));
}

/**
 * Placements of `workers` workers: the first k = min(8, available) logical processors of the
 * host, worker i on entry i mod k. The collector only copies placements into records and never
 * pins anything, so repeating a processor on a small host changes nothing but those fields.
 */
std::vector<logical_processor> test_placements(std::uint32_t workers) {
    const std::vector<logical_processor> processors = enumerate_logical_processors();
    const std::size_t k = processors.size() < k_race_workers ? processors.size() : k_race_workers;
    std::vector<logical_processor> placements;
    placements.reserve(workers);
    for (std::uint32_t i = 0; i < workers; ++i) {
        placements.push_back(processors[i % k]);
    }
    return placements;
}

/** A windowed verification's context: phase verify, RQ_ACCESS_VERIFY_AFTER_WRITE. */
access_context verify_context(std::uint32_t step, std::uint32_t pass, std::uint32_t worker,
                              std::uintptr_t region_base, std::uint64_t write_time_ns) noexcept {
    return access_context{step,   pass,        phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_WRITE,
                          worker, region_base, write_time_ns};
}

/**
 * Stores `actual` in the word, as a fault would have left it, then reports the mismatch the
 * way a verification kernel does. The collector's flushed re-read therefore loads `actual`.
 */
void report(worker_error_sink& sink, std::uint64_t* word, std::uint64_t expected,
            std::uint64_t actual) noexcept {
    *word = actual;
    sink.on_mismatch(word, expected, actual);
}

/**
 * One error_collector with its own region, stop signal, fake clock and counting -> real
 * layer stack: what a session's coordinator builds at run start, without the session. Members
 * are constructed in declaration order, so the collector, last, sees all of them.
 */
struct collector_rig {
    collector_rig(std::uint32_t detail_cap, bool stop_on_error, std::uint32_t step_count,
                  std::uint32_t workers)
        : placements(test_placements(workers)),
          memory(std::make_unique<counting_memory_access>(
              std::make_unique<real_memory_access>(host_kernels()))),
          collector(options(detail_cap, stop_on_error, step_count), stop, clock, *memory) {}

    /** The collector options for this rig's placements. */
    error_collector_options options(std::uint32_t detail_cap, bool stop_on_error,
                                    std::uint32_t step_count) const {
        error_collector_options result;
        result.detail_cap = detail_cap;
        result.stop_on_error = stop_on_error;
        result.step_count = step_count;
        result.placements = placements;
        return result;
    }

    /** Binds worker's sink to a verification of step and pass over this rig's region. */
    worker_error_sink& bound_sink(std::uint32_t worker, std::uint32_t step, std::uint32_t pass) {
        worker_error_sink& sink = collector.sink(worker);
        sink.bind(verify_context(step, pass, worker, region.base(), clock.now_ns()));
        return sink;
    }

    const std::vector<logical_processor> placements;
    region_buffer region;
    stop_signal stop;
    fake_clock clock;
    const std::unique_ptr<counting_memory_access> memory;
    error_collector collector;
};

/**
 * Every record delivered by a sequence of drains, with what the batches themselves showed.
 * Copies the records, since a batch only points into the collector's storage.
 */
struct drained_records {
    /** Drains once and appends the batch, checking its shape against the previous batches. */
    void drain_from(error_collector& collector) {
        const error_batch batch = collector.drain();
        ++drains;
        if ((batch.count == 0) != (batch.records == nullptr)) {
            ++malformed_batches;
        }
        if (batch.count != 0 && batch.records != nullptr) {
            if (batch.records[0].sequence != records.size()) {
                ++out_of_order_batches;
            }
            records.insert(records.end(), batch.records, batch.records + batch.count);
        }
        if (batch.total_errors < last_total || batch.total_errors < records.size()) {
            ++inconsistent_totals;
        }
        if (batch.has_news != (batch.count != 0 || batch.total_errors != last_total)) {
            ++wrong_news_flags;
        }
        last_total = batch.total_errors;
    }

    std::vector<rq_error_record> records;
    std::uint64_t last_total = 0;
    std::uint64_t drains = 0;
    /** Batches whose records pointer disagreed with their count. */
    std::uint64_t malformed_batches = 0;
    /** Batches that did not continue at the next undelivered sequence number. */
    std::uint64_t out_of_order_batches = 0;
    /** Batches whose total fell, or fell below the records delivered so far. */
    std::uint64_t inconsistent_totals = 0;
    /** Batches whose has_news disagreed with their count and total change. */
    std::uint64_t wrong_news_flags = 0;
};

/* ---------------------------------------------------------------------------------------------
 * Construction
 * ------------------------------------------------------------------------------------------- */

TEST(ErrorCollectorConstruction, RejectsMissingPlacementsAndZeroSteps) {
    stop_signal stop;
    fake_clock clock;
    real_memory_access memory(host_kernels());

    error_collector_options no_workers;
    no_workers.detail_cap = k_detail_cap;
    no_workers.step_count = 1U;
    const auto construct_without_workers = [&] {
        error_collector collector(no_workers, stop, clock, memory);
    };
    EXPECT_THROW(construct_without_workers(), std::invalid_argument);

    error_collector_options no_steps;
    no_steps.detail_cap = k_detail_cap;
    no_steps.step_count = 0U;
    no_steps.placements = test_placements(1U);
    const auto construct_without_steps = [&] {
        error_collector collector(no_steps, stop, clock, memory);
    };
    EXPECT_THROW(construct_without_steps(), std::invalid_argument);
}

TEST(ErrorCollectorConstruction, FreshCollectorHasNothingToReport) {
    collector_rig rig(k_detail_cap, false, 3U, 4U);
    ASSERT_TRUE(rig.region.valid());

    EXPECT_EQ(rig.collector.worker_count(), 4U);
    EXPECT_EQ(rig.collector.step_count(), 3U);
    EXPECT_EQ(rig.collector.detail_cap(), k_detail_cap);
    EXPECT_EQ(rig.collector.true_total(), 0U);
    EXPECT_EQ(rig.collector.stored_count(), 0U);
    EXPECT_FALSE(rig.collector.truncated());
    EXPECT_EQ(rig.collector.first_error_ms(), RQ_NO_FIRST_ERROR);
    for (std::uint32_t step = 0; step < 3U; ++step) {
        EXPECT_EQ(rig.collector.step_errors(step), 0U) << "step " << step;
    }
    for (std::uint32_t worker = 0; worker < 4U; ++worker) {
        EXPECT_EQ(rig.collector.sink(worker).worker_index(), worker);
    }

    const error_batch batch = rig.collector.drain();
    EXPECT_EQ(batch.count, 0U);
    EXPECT_EQ(batch.records, nullptr);
    EXPECT_EQ(batch.total_errors, 0U);
    EXPECT_FALSE(batch.has_news);
    EXPECT_FALSE(rig.stop.stop_requested());
    EXPECT_EQ(rig.memory->primitive_count(), 0U);
}

/* ---------------------------------------------------------------------------------------------
 * Cap and true totals
 * ------------------------------------------------------------------------------------------- */

TEST(ErrorCollectorCap, RacingWorkersKeepTheExactTotalAndStoreTheFirstCapRecordsOnce) {
    collector_rig rig(k_detail_cap, false, 1U, k_race_workers);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(0U, k_race_workers);

    // Worker w reports words w * 2000 + i, each k_race_stride_words apart, so every word is
    // distinct and its index gives back the expected and actual values it was reported with.
    std::latch start(k_race_workers);
    std::atomic<std::uint32_t> finished{0};
    std::vector<std::thread> workers;
    workers.reserve(k_race_workers);
    for (std::uint32_t w = 0; w < k_race_workers; ++w) {
        workers.emplace_back([&rig, &start, &finished, w] {
            worker_error_sink& sink = rig.bound_sink(w, 0U, 0U);
            start.arrive_and_wait();
            for (std::uint32_t i = 0; i < k_errors_per_worker; ++i) {
                const std::uint64_t n = std::uint64_t{w} * k_errors_per_worker + i;
                report(sink, rig.region.word(n * k_race_stride_words), expected_for(n),
                       actual_for(n));
            }
            finished.fetch_add(1U, std::memory_order_release);
        });
    }

    // The test thread is the coordinator: it drains while the workers race, as the session's
    // ticks do, then once more after they have joined, as the session does before Completed.
    drained_records drained;
    while (finished.load(std::memory_order_acquire) < k_race_workers) {
        drained.drain_from(rig.collector);
        std::this_thread::yield();
    }
    for (std::thread& worker : workers) {
        worker.join();
    }
    drained.drain_from(rig.collector);

    EXPECT_EQ(rig.collector.true_total(), k_race_total);
    EXPECT_EQ(rig.collector.step_errors(0U), k_race_total);
    EXPECT_EQ(rig.collector.stored_count(), k_detail_cap);
    EXPECT_TRUE(rig.collector.truncated());
    EXPECT_EQ(drained.last_total, k_race_total) << "the final drain must carry the true total";
    EXPECT_EQ(drained.malformed_batches, 0U);
    EXPECT_EQ(drained.out_of_order_batches, 0U) << "over " << drained.drains << " drains";
    EXPECT_EQ(drained.inconsistent_totals, 0U) << "over " << drained.drains << " drains";
    EXPECT_EQ(drained.wrong_news_flags, 0U) << "over " << drained.drains << " drains";
    // One re-read per mismatch, stored or not, and none counts as verified bytes.
    EXPECT_EQ(rig.memory->primitive_count(), k_race_total);
    EXPECT_EQ(rig.memory->verified_bytes(), 0U);

    ASSERT_EQ(drained.records.size(), std::size_t{k_detail_cap})
        << "true total " << rig.collector.true_total() << ", stored "
        << rig.collector.stored_count();
    std::set<std::uint64_t> addresses;
    for (std::size_t i = 0; i < drained.records.size(); ++i) {
        const rq_error_record& r = drained.records[i];
        ASSERT_EQ(r.sequence, i) << "records must arrive once each, in sequence order";
        const std::uint64_t offset = r.address - address_of(rig.region.word(0));
        ASSERT_EQ(offset % (k_race_stride_words * sizeof(std::uint64_t)), 0U)
            << "record " << i << " at " << hex64(r.address) << " was never reported";
        const std::uint64_t n = offset / (k_race_stride_words * sizeof(std::uint64_t));
        ASSERT_LT(n, k_race_total) << "record " << i << " at " << hex64(r.address);
        EXPECT_TRUE(addresses.insert(r.address).second)
            << "word " << hex64(r.address) << " stored twice";
        EXPECT_EQ(r.expected, expected_for(n)) << "record " << i;
        EXPECT_EQ(r.actual, actual_for(n)) << "record " << i;
        EXPECT_EQ(r.reread, actual_for(n)) << "record " << i;
        EXPECT_EQ(r.reread_outcome, RQ_REREAD_REPEATS_ACTUAL) << "record " << i;
        EXPECT_EQ(r.worker_index, n / k_errors_per_worker) << "record " << i;
    }

    const error_batch after = rig.collector.drain();
    EXPECT_EQ(after.count, 0U);
    EXPECT_FALSE(after.has_news);
    EXPECT_EQ(after.total_errors, k_race_total);
}

TEST(ErrorCollectorCap, ZeroCapStoresNothingAndKeepsEveryCountExact) {
    constexpr std::uint32_t k_workers = 2U;
    constexpr std::uint32_t k_per_worker = 25U;
    collector_rig rig(0U, false, 2U, k_workers);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(1U, k_workers);

    rig.clock.advance(42U * k_ns_per_ms);
    for (std::uint32_t w = 0; w < k_workers; ++w) {
        worker_error_sink& sink = rig.bound_sink(w, 1U, 0U);
        for (std::uint32_t i = 0; i < k_per_worker; ++i) {
            const std::uint64_t n = std::uint64_t{w} * k_per_worker + i;
            report(sink, rig.region.word(n * 8U), expected_for(n), actual_for(n));
        }
    }

    constexpr std::uint64_t k_total = std::uint64_t{k_workers} * k_per_worker;
    const error_batch batch = rig.collector.drain();
    EXPECT_EQ(batch.count, 0U);
    EXPECT_EQ(batch.records, nullptr);
    EXPECT_EQ(batch.total_errors, k_total);
    EXPECT_TRUE(batch.has_news) << "a rising total is news even with nothing stored";

    EXPECT_EQ(rig.collector.true_total(), k_total);
    EXPECT_EQ(rig.collector.stored_count(), 0U);
    EXPECT_TRUE(rig.collector.truncated());
    EXPECT_EQ(rig.collector.step_errors(0U), 0U);
    EXPECT_EQ(rig.collector.step_errors(1U), k_total);
    EXPECT_EQ(rig.collector.first_error_ms(), 42U);
    EXPECT_EQ(rig.memory->primitive_count(), k_total) << "every mismatch is still re-read";

    const error_batch quiet = rig.collector.drain();
    EXPECT_EQ(quiet.count, 0U);
    EXPECT_FALSE(quiet.has_news);
    EXPECT_EQ(quiet.total_errors, k_total);
}

/* ---------------------------------------------------------------------------------------------
 * Batch shape
 * ------------------------------------------------------------------------------------------- */

TEST(ErrorCollectorBatch, EachDrainDeliversOnlyNewRecordsThenTotalsPastTheCap) {
    constexpr std::uint32_t k_cap = 5U;
    collector_rig rig(k_cap, false, 1U, 1U);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(0U, 1U);
    worker_error_sink& sink = rig.bound_sink(0U, 0U, 0U);
    std::uint64_t reported = 0;
    const auto burst = [&](std::uint32_t count) {
        for (std::uint32_t i = 0; i < count; ++i, ++reported) {
            report(sink, rig.region.word(reported * 8U), expected_for(reported),
                   actual_for(reported));
        }
    };

    burst(3U);
    const error_batch first = rig.collector.drain();
    ASSERT_EQ(first.count, 3U);
    ASSERT_NE(first.records, nullptr);
    EXPECT_EQ(first.total_errors, 3U);
    EXPECT_TRUE(first.has_news);
    for (std::uint32_t i = 0; i < first.count; ++i) {
        EXPECT_EQ(first.records[i].sequence, i);
        EXPECT_EQ(first.records[i].address, address_of(rig.region.word(i * 8U)));
    }

    const error_batch idle = rig.collector.drain();
    EXPECT_EQ(idle.count, 0U);
    EXPECT_EQ(idle.records, nullptr);
    EXPECT_EQ(idle.total_errors, 3U);
    EXPECT_FALSE(idle.has_news) << "nothing new since the previous drain";

    // Crosses the cap: only the two records below it are delivered, the total is exact.
    burst(4U);
    const error_batch reaching = rig.collector.drain();
    ASSERT_EQ(reaching.count, 2U);
    ASSERT_NE(reaching.records, nullptr);
    EXPECT_EQ(reaching.records[0].sequence, 3U);
    EXPECT_EQ(reaching.records[1].sequence, 4U);
    EXPECT_EQ(reaching.total_errors, 7U);
    EXPECT_TRUE(reaching.has_news);
    EXPECT_EQ(rig.collector.stored_count(), k_cap);
    EXPECT_TRUE(rig.collector.truncated());

    burst(2U);
    const error_batch beyond = rig.collector.drain();
    EXPECT_EQ(beyond.count, 0U);
    EXPECT_EQ(beyond.records, nullptr);
    EXPECT_EQ(beyond.total_errors, 9U);
    EXPECT_TRUE(beyond.has_news) << "past the cap the rising total alone is news";

    const error_batch settled = rig.collector.drain();
    EXPECT_EQ(settled.count, 0U);
    EXPECT_EQ(settled.total_errors, 9U);
    EXPECT_FALSE(settled.has_news);

    // Slots are written once and never reused, so an earlier batch still reads its records.
    EXPECT_EQ(first.records[0].sequence, 0U);
    EXPECT_EQ(first.records[2].address, address_of(rig.region.word(16U)));
    EXPECT_EQ(rig.collector.true_total(), 9U);
}

/* ---------------------------------------------------------------------------------------------
 * Per-step counts
 * ------------------------------------------------------------------------------------------- */

TEST(ErrorCollectorStepCounts, PerStepCountsAreExactPastTheCap) {
    constexpr std::uint32_t k_cap = 10U;
    constexpr std::uint32_t k_steps = 4U;
    collector_rig rig(k_cap, false, k_steps, 2U);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    std::uint64_t reported = 0;
    const auto errors_in = [&](std::uint32_t step, std::uint32_t worker, std::uint32_t count) {
        worker_error_sink& sink = rig.bound_sink(worker, step, 0U);
        for (std::uint32_t i = 0; i < count; ++i, ++reported) {
            report(sink, rig.region.word(reported * 8U), expected_for(reported),
                   actual_for(reported));
        }
    };

    // Step 2 runs without errors; step 3 runs in Half mode, so one worker is active.
    rig.collector.begin_step(0U, 2U);
    errors_in(0U, 0U, 3U);
    errors_in(0U, 1U, 2U);
    rig.collector.begin_step(1U, 2U);
    errors_in(1U, 1U, 7U);
    rig.collector.begin_step(2U, 2U);
    rig.collector.begin_step(3U, 1U);
    errors_in(3U, 0U, 11U);

    EXPECT_EQ(rig.collector.step_errors(0U), 5U);
    EXPECT_EQ(rig.collector.step_errors(1U), 7U);
    EXPECT_EQ(rig.collector.step_errors(2U), 0U);
    EXPECT_EQ(rig.collector.step_errors(3U), 11U);
    EXPECT_EQ(rig.collector.step_errors(k_steps), 0U) << "a step index past the plan counts 0";
    EXPECT_EQ(rig.collector.step_errors(UINT32_MAX), 0U);
    EXPECT_EQ(rig.collector.true_total(), 23U);
    EXPECT_EQ(rig.collector.stored_count(), k_cap);
    EXPECT_TRUE(rig.collector.truncated());

    // The stored detail is the first ten errors in detection order: five of step 0, then five
    // of step 1; none of step 3's errors fits under the cap.
    drained_records drained;
    drained.drain_from(rig.collector);
    ASSERT_EQ(drained.records.size(), std::size_t{k_cap});
    for (std::size_t i = 0; i < drained.records.size(); ++i) {
        EXPECT_EQ(drained.records[i].step_index, i < 5U ? 0U : 1U) << "record " << i;
    }
    EXPECT_EQ(drained.last_total, 23U);
}

/* ---------------------------------------------------------------------------------------------
 * first_error_ms
 * ------------------------------------------------------------------------------------------- */

TEST(ErrorCollectorFirstError, FirstErrorTimeIsTheTestTimeOfTheEarliestError) {
    collector_rig rig(k_detail_cap, false, 1U, 1U);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(0U, 1U);
    worker_error_sink& sink = rig.bound_sink(0U, 0U, 0U);
    EXPECT_EQ(rig.collector.first_error_ms(), RQ_NO_FIRST_ERROR) << "no error confirmed yet";

    rig.clock.advance(1'234U * k_ns_per_ms);
    EXPECT_EQ(rig.collector.first_error_ms(), RQ_NO_FIRST_ERROR) << "time alone sets nothing";
    report(sink, rig.region.word(0U), expected_for(0U), actual_for(0U));
    EXPECT_EQ(rig.collector.first_error_ms(), 1'234U);

    rig.clock.advance(500U * k_ns_per_ms);
    report(sink, rig.region.word(8U), expected_for(1U), actual_for(1U));
    rig.clock.advance(2'000U * k_ns_per_ms);
    report(sink, rig.region.word(16U), expected_for(2U), actual_for(2U));
    EXPECT_EQ(rig.collector.first_error_ms(), 1'234U) << "later errors never move it";

    drained_records drained;
    drained.drain_from(rig.collector);
    ASSERT_EQ(drained.records.size(), 3U);
    EXPECT_EQ(drained.records[0].time_into_test_ms, 1'234U);
    EXPECT_EQ(drained.records[1].time_into_test_ms, 1'734U);
    EXPECT_EQ(drained.records[2].time_into_test_ms, 3'734U);
}

/* ---------------------------------------------------------------------------------------------
 * Stop on error
 * ------------------------------------------------------------------------------------------- */

TEST(ErrorCollectorStopOnError, FirstMismatchRequestsTheStopBeforeOnMismatchReturns) {
    collector_rig rig(k_detail_cap, true, 2U, 1U);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(1U, 1U);
    worker_error_sink& sink = rig.bound_sink(0U, 1U, 3U);
    ASSERT_FALSE(rig.stop.stop_requested());
    ASSERT_EQ(rig.stop.pending_reason(), 0U);

    // No drain and no callback runs: the worker's own call has requested the stop on return.
    report(sink, rig.region.word(64U), expected_for(0U), actual_for(0U));
    EXPECT_EQ(rig.stop.pending_reason(), RQ_END_FIRST_ERROR);
    EXPECT_TRUE(rig.stop.stop_requested());
    EXPECT_EQ(WaitForSingleObject(rig.stop.event_handle(), 0), WAIT_OBJECT_0)
        << "holds and other stop waits must wake";

    const stop_signal::end_info first = rig.stop.snapshot();
    EXPECT_EQ(first.reason, RQ_END_FIRST_ERROR);
    EXPECT_EQ(first.os_error, 0U);
    EXPECT_NE(first.detail[0], '\0') << "every end reason but completed carries a diagnostic";

    // Errors confirmed before the workers observe the flag are still counted and stored, and
    // the reason, first writer wins, is never replaced.
    report(sink, rig.region.word(128U), expected_for(1U), actual_for(1U));
    EXPECT_EQ(rig.collector.true_total(), 2U);
    EXPECT_EQ(rig.collector.stored_count(), 2U);
    EXPECT_FALSE(rig.stop.request(RQ_END_USER_STOP));
    const stop_signal::end_info later = rig.stop.snapshot();
    EXPECT_EQ(later.reason, RQ_END_FIRST_ERROR);
    EXPECT_EQ(std::string(later.detail), std::string(first.detail));
}

/** A first error's verification context and the end detail Stop on error must record for it. */
struct first_error_detail_case {
    const char* name;
    std::uint32_t step_count;
    std::uint32_t workers;
    std::uint32_t worker;
    std::uint32_t step;
    std::uint32_t pass;
    /** The detail up to the word's address: step and pass from 1, the worker from 0. */
    const char* expected_prefix;
};

void PrintTo(const first_error_detail_case& detail_case, std::ostream* os) {
    *os << detail_case.name << " (worker " << detail_case.worker << ", step index "
        << detail_case.step << ", pass index " << detail_case.pass << ")";
}

class ErrorCollectorFirstErrorDetail : public ::testing::TestWithParam<first_error_detail_case> {};

TEST_P(ErrorCollectorFirstErrorDetail, NumbersStepAndPassAsTheResultsLogAndNamesTheWord) {
    const first_error_detail_case& detail_case = GetParam();
    collector_rig rig(k_detail_cap, true, detail_case.step_count, detail_case.workers);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(detail_case.step, detail_case.workers);
    worker_error_sink& sink =
        rig.bound_sink(detail_case.worker, detail_case.step, detail_case.pass);

    std::uint64_t* const word = rig.region.word(4'096U);
    report(sink, word, expected_for(0U), actual_for(0U));

    // The results log writes this error as step=<step + 1>/<count> ... pass=<pass + 1> ...
    // worker=<worker>; the detail must number it the same way.
    const stop_signal::end_info info = rig.stop.snapshot();
    ASSERT_EQ(info.reason, RQ_END_FIRST_ERROR);
    EXPECT_EQ(std::string(info.detail),
              std::string(detail_case.expected_prefix) + hex64(address_of(word)));
}

INSTANTIATE_TEST_SUITE_P(
    ErrorCollector, ErrorCollectorFirstErrorDetail,
    ::testing::Values(
        first_error_detail_case{"FirstStepFirstPass", 2U, 2U, 1U, 0U, 0U,
                                "First error: step 1 of 2, pass 1, worker 1, virtual address "},
        first_error_detail_case{"LaterStepAndPass", 3U, 3U, 2U, 1U, 3U,
                                "First error: step 2 of 3, pass 4, worker 2, virtual address "},
        first_error_detail_case{
            "LastPassIndex", 1U, 1U, 0U, 0U, 0xFFFF'FFFFU,
            "First error: step 1 of 1, pass 4294967296, worker 0, virtual address "}),
    [](const ::testing::TestParamInfo<first_error_detail_case>& info) {
        return std::string(info.param.name);
    });

TEST(ErrorCollectorStopOnError, RacingWorkersRecordFirstErrorOnce) {
    constexpr std::uint32_t k_per_worker = 500U;
    collector_rig rig(k_detail_cap, true, 1U, k_race_workers);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(0U, k_race_workers);

    std::latch start(k_race_workers);
    std::vector<std::thread> workers;
    workers.reserve(k_race_workers);
    for (std::uint32_t w = 0; w < k_race_workers; ++w) {
        workers.emplace_back([&rig, &start, w] {
            worker_error_sink& sink = rig.bound_sink(w, 0U, 0U);
            start.arrive_and_wait();
            for (std::uint32_t i = 0; i < k_per_worker; ++i) {
                const std::uint64_t n = std::uint64_t{w} * k_per_worker + i;
                report(sink, rig.region.word(n * k_race_stride_words), expected_for(n),
                       actual_for(n));
            }
        });
    }
    for (std::thread& worker : workers) {
        worker.join();
    }

    constexpr std::uint64_t k_total = std::uint64_t{k_race_workers} * k_per_worker;
    EXPECT_EQ(rig.collector.true_total(), k_total) << "errors after the request still count";
    EXPECT_EQ(rig.collector.stored_count(), k_total);
    EXPECT_EQ(rig.stop.pending_reason(), RQ_END_FIRST_ERROR);
    EXPECT_TRUE(rig.stop.stop_requested());

    const stop_signal::end_info recorded = rig.stop.snapshot();
    EXPECT_EQ(recorded.reason, RQ_END_FIRST_ERROR);
    EXPECT_NE(recorded.detail[0], '\0');
    EXPECT_FALSE(rig.stop.request(RQ_END_USER_STOP)) << "first writer wins";
    const stop_signal::end_info after = rig.stop.snapshot();
    EXPECT_EQ(after.reason, RQ_END_FIRST_ERROR);
    EXPECT_EQ(std::memcmp(after.detail, recorded.detail, sizeof(after.detail)), 0)
        << "the recorded diagnostic must be one request's, written once";
}

/** An end reason a host or a fault records before the run's first error. */
struct earlier_stop {
    const char* name;
    std::uint32_t reason;
};

void PrintTo(const earlier_stop& stop, std::ostream* os) {
    *os << stop.name << " (end reason " << stop.reason << ")";
}

class ErrorCollectorEarlierStop : public ::testing::TestWithParam<earlier_stop> {};

TEST_P(ErrorCollectorEarlierStop, KeepsItsReasonAndErrorsStillCount) {
    collector_rig rig(k_detail_cap, true, 1U, 1U);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(0U, 1U);
    worker_error_sink& sink = rig.bound_sink(0U, 0U, 0U);
    ASSERT_TRUE(rig.stop.request(GetParam().reason, 0U, "recorded before the first error"));

    report(sink, rig.region.word(0U), expected_for(0U), actual_for(0U));
    report(sink, rig.region.word(8U), expected_for(1U), actual_for(1U));

    EXPECT_EQ(rig.stop.pending_reason(), GetParam().reason);
    const stop_signal::end_info info = rig.stop.snapshot();
    EXPECT_EQ(info.reason, GetParam().reason);
    EXPECT_STREQ(info.detail, "recorded before the first error");
    EXPECT_EQ(rig.collector.true_total(), 2U);
    EXPECT_EQ(rig.collector.stored_count(), 2U);
}

INSTANTIATE_TEST_SUITE_P(ErrorCollector, ErrorCollectorEarlierStop,
                         ::testing::Values(earlier_stop{"UserStop", RQ_END_USER_STOP},
                                           earlier_stop{"Thermal", RQ_END_THERMAL},
                                           earlier_stop{"HostFault", RQ_END_HOST_FAULT}),
                         [](const ::testing::TestParamInfo<earlier_stop>& info) {
                             return std::string(info.param.name);
                         });

TEST(ErrorCollectorStopOnError, OffRequestsNoStop) {
    constexpr std::uint32_t k_errors = 100U;
    collector_rig rig(k_detail_cap, false, 1U, 2U);
    ASSERT_TRUE(rig.region.valid());
    rig.collector.begin_run(rig.clock.now_ns());
    rig.collector.begin_step(0U, 2U);
    for (std::uint32_t i = 0; i < k_errors; ++i) {
        worker_error_sink& sink = rig.bound_sink(i % 2U, 0U, 0U);
        report(sink, rig.region.word(std::size_t{i} * 8U), expected_for(i), actual_for(i));
    }

    EXPECT_EQ(rig.collector.true_total(), k_errors);
    EXPECT_FALSE(rig.stop.stop_requested());
    EXPECT_EQ(rig.stop.pending_reason(), 0U);
    EXPECT_EQ(WaitForSingleObject(rig.stop.event_handle(), 0), static_cast<DWORD>(WAIT_TIMEOUT));
}

/* ---------------------------------------------------------------------------------------------
 * Record fields
 * ------------------------------------------------------------------------------------------- */

/** One mismatch of the record test and the context it is verified under. */
struct record_case {
    const char* name;
    std::uint32_t worker;
    std::uint32_t step;
    std::uint32_t pass;
    phase_kind phase;
    std::uint32_t access_type;
    /** begin_step's active_workers for the case's step. */
    std::uint32_t active_workers;
    std::uintptr_t region_base;
    std::size_t word;
    std::uint64_t flip_mask;
    /** Fake time between the context's write stamp and the mismatch. */
    std::uint64_t age_ns;
    /** The logical offset the record must carry: address - region_base, modulo 2^64. */
    std::uint64_t region_offset;
};

TEST(ErrorCollectorRecord, FieldsComeFromTheContextTheSinkItsPlacementAndTheStep) {
    constexpr std::uint32_t k_workers = 3U;
    collector_rig rig(k_detail_cap, false, 3U, k_workers);
    ASSERT_TRUE(rig.region.valid());
    const std::uint64_t t0 = rig.clock.now_ns();
    rig.collector.begin_run(t0);

    const std::uintptr_t base = rig.region.base();
    constexpr std::uintptr_t k_chunk_offset = std::uintptr_t{32} << 20U;
    // The wrapping chunk's logical offset exceeds its address, so its logical base wraps below
    // zero, as core/memory/memory_region.h allows.
    const std::uintptr_t wrapped_base =
        std::uintptr_t{0} - static_cast<std::uintptr_t>(k_wrap_margin);
    const record_case cases[] = {
        {"OffsetZeroChunk", 0U, 1U, 2U, phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_WRITE, 3U, base,
         1'000U, std::uint64_t{1} << 17U, 1'234'567U, 8'000U},
        {"ChunkAt32MiB", 1U, 1U, 0U, phase_kind::sweep, RQ_ACCESS_READ_MODIFY_WRITE, 3U,
         base - k_chunk_offset, 4'096U, 0x00F0000000000F00ULL, 750'000U,
         k_chunk_offset + 4'096U * sizeof(std::uint64_t)},
        {"WrappingChunk", 2U, 1U, 5U, phase_kind::turnaround, RQ_ACCESS_TURNAROUND_READ, 3U,
         wrapped_base, 77U, std::uint64_t{1} << 63U, 999U,
         address_of(rig.region.word(77U)) + k_wrap_margin},
        {"HalfModeHold", 0U, 2U, 1U, phase_kind::verify, RQ_ACCESS_VERIFY_AFTER_HOLD, 2U, base,
         123'456U, 1U, 5'000U * k_ns_per_ms, 123'456U * sizeof(std::uint64_t)},
    };

    std::vector<std::uint64_t> detected_ns;
    std::uint32_t current_step = UINT32_MAX;
    std::uint64_t n = 0;
    for (const record_case& c : cases) {
        if (c.step != current_step) {
            rig.collector.begin_step(c.step, c.active_workers);
            current_step = c.step;
        }
        worker_error_sink& sink = rig.collector.sink(c.worker);
        sink.bind(access_context{c.step, c.pass, c.phase, c.access_type, c.worker, c.region_base,
                                 rig.clock.now_ns()});
        rig.clock.advance(c.age_ns);
        detected_ns.push_back(rig.clock.now_ns());
        report(sink, rig.region.word(c.word), expected_for(n), expected_for(n) ^ c.flip_mask);
        ++n;
    }

    drained_records drained;
    drained.drain_from(rig.collector);
    ASSERT_EQ(drained.records.size(), std::size(cases));
    for (std::size_t i = 0; i < std::size(cases); ++i) {
        const record_case& c = cases[i];
        const rq_error_record& r = drained.records[i];
        SCOPED_TRACE(c.name);
        const std::uint64_t expected = expected_for(i);
        const std::uint64_t actual = expected ^ c.flip_mask;
        EXPECT_EQ(r.sequence, i);
        EXPECT_EQ(r.address, address_of(rig.region.word(c.word)));
        EXPECT_EQ(r.region_offset, c.region_offset)
            << hex64(r.region_offset) << " against " << hex64(c.region_offset);
        EXPECT_EQ(r.expected, expected);
        EXPECT_EQ(r.actual, actual);
        EXPECT_EQ(r.reread, actual) << "the flushed re-read loads the stored wrong value";
        EXPECT_EQ(r.bit_mask, expected ^ actual);
        EXPECT_EQ(r.time_since_write_us, c.age_ns / k_ns_per_us);
        EXPECT_EQ(r.time_into_test_ms, (detected_ns[i] - t0) / k_ns_per_ms);
        EXPECT_EQ(r.step_index, c.step);
        EXPECT_EQ(r.pass_index, c.pass);
        EXPECT_EQ(r.access_type, c.access_type);
        EXPECT_EQ(r.reread_outcome, RQ_REREAD_REPEATS_ACTUAL);
        EXPECT_EQ(r.address_kind, RQ_ADDRESS_KIND_VIRTUAL);
        EXPECT_EQ(r.worker_index, c.worker);
        EXPECT_EQ(r.logical_processor, std::uint32_t{rig.placements[c.worker].number});
        EXPECT_EQ(r.processor_group, std::uint32_t{rig.placements[c.worker].group});
        EXPECT_EQ(r.active_workers, c.active_workers);
        EXPECT_EQ(r.reserved0, 0U);
    }
}

/* ---------------------------------------------------------------------------------------------
 * Batching through a session
 * ------------------------------------------------------------------------------------------- */

/** Planned region of the session test: 128 MiB per worker. */
constexpr std::uint64_t k_session_region_bytes = std::uint64_t{256} << 20U;
/** Floor a lock shortfall may shrink the region to. */
constexpr std::uint64_t k_session_min_region_bytes = std::uint64_t{64} << 20U;
constexpr std::uint32_t k_session_workers = 2U;
constexpr std::uint32_t k_session_step_ms = 3'000U;

/**
 * The injected range: a contiguous 1/32 of the region, 8 MiB in. It lies in worker 0's slice
 * even if locking shrinks the region to its floor, so every target is verified on every pass.
 */
constexpr std::uint64_t k_session_fault_offset = std::uint64_t{8} << 20U;
constexpr std::uint64_t k_session_fault_length = k_session_region_bytes / 32U;

/**
 * Corruptions per pass and in the whole run. Three passes pass the 10,000-record cap, which even
 * a Debug build on a loaded host completes well within the step, and the 20,000 injections
 * spread over five passes, so errors keep arriving across several drain intervals rather than
 * in one burst.
 */
constexpr std::uint32_t k_session_per_pass = 4'000U;
constexpr std::uint64_t k_session_max_injections = 20'000U;

/** Smallest accepted spacing of two on_errors calls: 100 ms less 10 ms of scheduling noise. */
constexpr std::uint64_t k_min_batch_spacing_ns = k_error_batch_interval_ns - 10U * k_ns_per_ms;
constexpr double k_max_batches_per_second = 11.0;

/** How long the test waits for on_released before it stops the run, and then after the stop. */
constexpr std::chrono::seconds k_run_timeout{120};
constexpr std::chrono::seconds k_stop_timeout{30};

static_assert(k_session_fault_offset + k_session_fault_length <=
                  k_session_min_region_bytes / k_session_workers,
              "the injected range must stay inside worker 0's slice at the region floor");
static_assert(k_session_max_injections > k_detail_cap &&
                  k_session_max_injections > k_session_per_pass,
              "the session test must exceed the detail cap over several passes");

/**
 * Budget of the stop test's step: far longer than the test lets it run, so the host's stop
 * always lands mid-step.
 */
constexpr std::uint32_t k_stop_step_ms = 60'000U;

/** The bit the stop test's single-bit flips corrupt, as in the fault-class tests. */
constexpr std::uint32_t k_stop_flip_bit = 17U;

/**
 * Corruptions the stop test may inject in its whole run, spread over the whole region, so
 * errors keep arriving through every verification until the stop lands.
 */
constexpr std::uint64_t k_stop_max_injections = 200'000U;

/** Batches the stop test lets arrive before it may stop the run, so errors are flowing. */
constexpr std::size_t k_batches_before_stop = 3U;

/**
 * The stop test stops at a progress tick that finds undelivered errors while the last batch is
 * younger than this, so the stop's records fall due inside the 100 ms spacing.
 */
constexpr std::uint64_t k_stop_window_ns = k_error_batch_interval_ns / 2U;

/** The stop bound: Completed within 2 s of the request. */
constexpr std::uint64_t k_stop_bound_ns = 2'000U * k_ns_per_ms;

static_assert(k_stop_max_injections > k_detail_cap,
              "the stop test's errors must be able to pass the detail cap");

/** One on_errors call as the session raised it. */
struct error_call {
    /** Session-clock (QPC) time at which the call arrived. */
    std::uint64_t at_ns;
    std::uint32_t count;
    bool has_records;
    std::uint64_t total_errors;
    /** on_completed had already been raised when the call arrived. */
    bool after_completed;
};

/**
 * What the session's callbacks saw: written on the callback thread under the mutex, read by
 * the test thread once on_released has been raised. A callback whose own bookkeeping throws
 * sets callback_failed instead, since nothing may unwind into the engine.
 */
struct session_log {
    explicit session_log(const IClock& qpc) : clock(qpc) {
        error_calls.reserve(1024U);
        records.reserve(k_detail_cap);
    }

    const IClock& clock;
    std::mutex mutex;
    std::condition_variable changed;
    std::set<DWORD> callback_threads;
    std::vector<error_call> error_calls;
    std::vector<rq_error_record> records;
    rq_run_result result{};
    std::uint64_t completed_ns = 0;
    bool completed = false;
    bool released = false;
    std::atomic<bool> callback_failed{false};

    // Stop test only: the session log_progress_and_stop stops, and what that stop saw.
    rq_session_handle stop_handle = nullptr;
    /** Session-clock time of the stop request; 0 until it is made. */
    std::uint64_t stop_ns = 0;
    rq_result stop_result = RQ_OK;
    /** on_errors calls that had arrived when the stop was requested. */
    std::size_t batches_at_stop = 0;
};

/** Runs body on the log under its mutex, noting the calling thread, then wakes the test. */
template <typename Body>
void with_log(void* user_data, Body body) noexcept {
    session_log& log = *static_cast<session_log*>(user_data);
    try {
        std::lock_guard<std::mutex> lock(log.mutex);
        log.callback_threads.insert(GetCurrentThreadId());
        body(log);
    } catch (...) {
        log.callback_failed.store(true, std::memory_order_relaxed);
    }
    log.changed.notify_all();
}

void RQ_CALL log_prepared(void* user_data, const rq_prepared* /*info*/) noexcept {
    with_log(user_data, [](session_log&) {});
}

std::uint32_t RQ_CALL log_step_starting(void* user_data, const rq_step_start* /*info*/) noexcept {
    with_log(user_data, [](session_log&) {});
    return RQ_ACTION_CONTINUE;
}

void RQ_CALL log_step_finished(void* user_data, const rq_step_result* /*result*/) noexcept {
    with_log(user_data, [](session_log&) {});
}

void RQ_CALL log_progress(void* user_data, const rq_progress* /*progress*/) noexcept {
    with_log(user_data, [](session_log&) {});
}

/**
 * on_progress of the stop test. Requests the host stop once, at the first tick whose true total
 * exceeds what the last batch carried while that batch is younger than k_stop_window_ns: the
 * stop lands mid-step with errors still undelivered, so the run's final batch is due before
 * the spacing has elapsed. rq_session_stop may be called from inside a callback.
 */
void RQ_CALL log_progress_and_stop(void* user_data, const rq_progress* progress) noexcept {
    const std::uint64_t at_ns = static_cast<session_log*>(user_data)->clock.now_ns();
    with_log(user_data, [&](session_log& log) {
        if (log.stop_ns != 0 || log.error_calls.size() < k_batches_before_stop) {
            return;
        }
        const error_call& last = log.error_calls.back();
        if (progress->total_errors > last.total_errors && at_ns - last.at_ns < k_stop_window_ns) {
            log.stop_ns = at_ns;
            log.batches_at_stop = log.error_calls.size();
            log.stop_result = session_stop(log.stop_handle, RQ_STOP_USER);
        }
    });
}

void RQ_CALL log_errors(void* user_data, const rq_error_record* records, std::uint32_t count,
                        std::uint64_t total_errors) noexcept {
    // Read before the lock, so the spacing measured is the calls' arrival.
    const std::uint64_t at_ns = static_cast<session_log*>(user_data)->clock.now_ns();
    with_log(user_data, [&](session_log& log) {
        log.error_calls.push_back(
            error_call{at_ns, count, records != nullptr, total_errors, log.completed});
        if (records != nullptr) {
            log.records.insert(log.records.end(), records, records + count);
        }
    });
}

void RQ_CALL log_completed(void* user_data, const rq_run_result* result) noexcept {
    const std::uint64_t at_ns = static_cast<session_log*>(user_data)->clock.now_ns();
    with_log(user_data, [&](session_log& log) {
        log.result = *result;
        log.completed_ns = at_ns;
        log.completed = true;
    });
}

void RQ_CALL log_released(void* user_data, std::uint64_t /*released_bytes*/,
                          std::uint64_t /*release_ms*/) noexcept {
    with_log(user_data, [](session_log& log) { log.released = true; });
}

/** Waits until on_released has been raised, or the timeout elapses. */
bool wait_for_release(session_log& log, std::chrono::seconds timeout) {
    std::unique_lock<std::mutex> lock(log.mutex);
    return log.changed.wait_for(lock, timeout, [&log] { return log.released; });
}

/**
 * The thread that issued each worker's primitives, recorded by the counting layer's observer
 * on every call, the re-reads included. Lock-free and allocation-free, as an observer must be.
 */
struct worker_thread_probe {
    void observe(std::uint32_t worker) noexcept {
        if (worker >= threads.size()) {
            foreign_worker.store(true, std::memory_order_relaxed);
            return;
        }
        const DWORD self = GetCurrentThreadId();
        DWORD seen = 0;
        if (!threads[worker].compare_exchange_strong(seen, self, std::memory_order_relaxed) &&
            seen != self) {
            changed_thread.store(true, std::memory_order_relaxed);
        }
    }

    std::array<std::atomic<DWORD>, k_session_workers> threads{};
    /** A primitive carried a worker index outside the session's workers. */
    std::atomic<bool> foreign_worker{false};
    /** One worker's primitives came from more than one thread. */
    std::atomic<bool> changed_thread{false};
};

/** Destroys the session on scope exit, so a failed assertion never leaves a run going. */
class session_handle_guard {
public:
    session_handle_guard() = default;
    ~session_handle_guard() { reset(); }

    session_handle_guard(const session_handle_guard&) = delete;
    session_handle_guard& operator=(const session_handle_guard&) = delete;

    rq_session_handle* out() noexcept { return &handle_; }
    rq_session_handle get() const noexcept { return handle_; }

    /**
     * Destroys the session, if any. A throwing session_destroy is reported as RQ_E_INTERNAL, as
     * the ABI boundary maps it, rather than escaping the destructor.
     */
    rq_result reset() noexcept {
        if (handle_ == nullptr) {
            return RQ_OK;
        }
        rq_result result = RQ_E_INTERNAL;
        try {
            result = session_destroy(handle_);
        } catch (...) {
            result = RQ_E_INTERNAL;
        }
        handle_ = nullptr;
        return result;
    }

private:
    rq_session_handle handle_ = nullptr;
};

TEST(ErrorCollectorSession, BatchesAtMostTenTimesASecondOnTheCoordinatorWithExactTotals) {
    const auto clock = std::make_shared<qpc_clock>();
    session_log log(*clock);
    worker_thread_probe probe;
    counting_memory_access* counting = nullptr;
    fault_injecting_memory_access* faults = nullptr;

    rq_fault_spec spec = make_fault_spec(RQ_FAULT_ADDRESS_REGION);
    spec.per_pass_count = k_session_per_pass;
    spec.max_injections = k_session_max_injections;
    spec.region_offset = k_session_fault_offset;
    spec.region_length = k_session_fault_length;
    spec.seed = 0x0123456789ABCDEFULL;
    const char* invalid = nullptr;
    ASSERT_TRUE(validate_fault_spec(spec, k_session_region_bytes, &invalid)) << invalid;

    // fault-injecting -> counting -> real, the test DLL's stack, with the session's own clock.
    session_dependencies dependencies;
    dependencies.clock = clock;
    dependencies.wrap_memory_access =
        [&](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
        auto counted = std::make_unique<counting_memory_access>(std::move(real));
        counted->set_observer(
            [&probe](const primitive_record& record) { probe.observe(record.worker); });
        counting = counted.get();
        auto faulted = std::make_unique<fault_injecting_memory_access>(
            std::move(counted), normalized(spec, k_session_region_bytes), *clock);
        faults = faulted.get();
        return faulted;
    };

    rq_step_def step{};
    copy_utf8_truncated(step.name, sizeof(step.name), "Random sequential sweep");
    step.pattern = RQ_PATTERN_RANDOM;
    step.access_order = RQ_ORDER_SEQUENTIAL_ASCENDING;
    step.thread_mode = RQ_THREADS_ALL;
    step.hold_ms = 0U;
    step.time_budget_ms = k_session_step_ms;
    step.coverage_budget_bp = 2'500U;

    rq_session_config config{};
    config.struct_size = sizeof(rq_session_config);
    config.step_def_size = sizeof(rq_step_def);
    config.region_bytes = k_session_region_bytes;
    config.min_region_bytes = k_session_min_region_bytes;
    config.seed = 0x5A3C96E1F00F7EC3ULL;
    config.worker_count = k_session_workers;
    config.stop_on_error = 0U;
    config.error_detail_cap = k_detail_cap;
    config.progress_interval_ms = 250U;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = 1U;
    config.steps = &step;

    rq_callbacks callbacks{};
    callbacks.struct_size = sizeof(rq_callbacks);
    callbacks.user_data = &log;
    callbacks.on_prepared = &log_prepared;
    callbacks.on_step_starting = &log_step_starting;
    callbacks.on_step_finished = &log_step_finished;
    callbacks.on_progress = &log_progress;
    callbacks.on_errors = &log_errors;
    callbacks.on_completed = &log_completed;
    callbacks.on_released = &log_released;

    session_handle_guard handle;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), handle.out()), RQ_OK);
    ASSERT_NE(faults, nullptr);
    ASSERT_NE(counting, nullptr);
    const std::uint64_t start_ns = clock->now_ns();
    ASSERT_EQ(session_start(handle.get()), RQ_OK);
    bool released = wait_for_release(log, k_run_timeout);
    if (!released) {
        EXPECT_EQ(session_stop(handle.get(), RQ_STOP_USER), RQ_OK);
        released = wait_for_release(log, k_stop_timeout);
    }
    ASSERT_TRUE(released) << "the session never raised on_released";

    // The run has quiesced: read the layers and the session-owned step results before destroy.
    rq_run_result result{};
    result.struct_size = sizeof(rq_run_result);
    ASSERT_EQ(session_get_result(handle.get(), &result), RQ_OK);
    ASSERT_EQ(result.step_result_count, 1U);
    const rq_step_result step_result = result.step_results[0];
    const std::uint64_t injected = faults->injection_count();
    const std::uint64_t verified_opportunities = faults->verified_opportunity_count();
    const std::uint64_t independent_verified_bytes = counting->verified_bytes();
    ASSERT_EQ(handle.reset(), RQ_OK);

    // No callback runs after destroy, so the log is stable from here.
    std::lock_guard<std::mutex> lock(log.mutex);
    EXPECT_FALSE(log.callback_failed.load(std::memory_order_relaxed));
    ASSERT_TRUE(log.completed);
    EXPECT_EQ(result.end_reason, RQ_END_COMPLETED) << result.end_detail;
    EXPECT_EQ(log.result.total_errors, result.total_errors);
    EXPECT_GT(independent_verified_bytes, 0U) << "the step must have verified memory";

    // Ground truth: each logged corruption had its verification and is exactly one error.
    EXPECT_EQ(verified_opportunities, injected);
    EXPECT_EQ(result.total_errors, verified_opportunities)
        << "run total " << result.total_errors << ", verified-opportunity injections "
        << verified_opportunities;
    EXPECT_GT(result.total_errors, std::uint64_t{k_detail_cap})
        << "the cap must be exercised; " << step_result.passes << " passes of "
        << k_session_per_pass << " injections ran";
    EXPECT_EQ(step_result.errors, result.total_errors);

    const std::vector<error_call>& calls = log.error_calls;
    ASSERT_FALSE(calls.empty()) << "errors were confirmed but no batch was raised";
    EXPECT_EQ(calls.back().total_errors, result.total_errors)
        << "the last batch must carry the final true total";
    std::uint64_t delivered = 0;
    std::uint64_t min_spacing_ns = UINT64_MAX;
    for (std::size_t i = 0; i < calls.size(); ++i) {
        const error_call& call = calls[i];
        EXPECT_EQ(call.count == 0U, !call.has_records) << "call " << i;
        if (delivered >= k_detail_cap) {
            EXPECT_EQ(call.count, 0U) << "call " << i << " carries records past the cap";
        }
        if (i > 0U) {
            const error_call& previous = calls[i - 1U];
            const std::uint64_t spacing_ns = call.at_ns - previous.at_ns;
            min_spacing_ns = spacing_ns < min_spacing_ns ? spacing_ns : min_spacing_ns;
            EXPECT_GE(spacing_ns, k_min_batch_spacing_ns)
                << "on_errors calls " << i - 1U << " and " << i << " arrived "
                << static_cast<double>(spacing_ns) / static_cast<double>(k_ns_per_ms)
                << " ms apart";
            EXPECT_GE(call.total_errors, previous.total_errors)
                << "the total fell from " << previous.total_errors << " to " << call.total_errors
                << " at call " << i;
            if (call.count == 0U) {
                EXPECT_GT(call.total_errors, previous.total_errors)
                    << "call " << i << " has no records, so only a rising total justifies it";
            }
        }
        delivered += call.count;
        EXPECT_GE(call.total_errors, delivered) << "call " << i;
    }
    EXPECT_EQ(delivered, std::uint64_t{k_detail_cap})
        << "stored records delivered over " << calls.size() << " batches";
    // Reported in the XML results, so a run's batch cadence can be read without a failure.
    RecordProperty("error_batches", std::to_string(calls.size()));
    RecordProperty("min_batch_spacing_ms", calls.size() >= 2U
                                               ? std::to_string(min_spacing_ns / k_ns_per_ms)
                                               : std::string("n/a"));
    RecordProperty("total_errors", std::to_string(result.total_errors));
    RecordProperty("passes", std::to_string(step_result.passes));

    const double run_seconds = static_cast<double>(log.completed_ns - start_ns) /
                               static_cast<double>(1'000U * k_ns_per_ms);
    EXPECT_LE(static_cast<double>(calls.size()) / run_seconds, k_max_batches_per_second)
        << calls.size() << " batches in " << run_seconds << " s";
    if (calls.size() >= 2U) {
        const double span_seconds = static_cast<double>(calls.back().at_ns - calls.front().at_ns) /
                                    static_cast<double>(1'000U * k_ns_per_ms);
        EXPECT_LE(static_cast<double>(calls.size() - 1U) / span_seconds, k_max_batches_per_second)
            << calls.size() << " batches over " << span_seconds << " s";
    }

    ASSERT_EQ(log.records.size(), std::size_t{k_detail_cap});
    for (std::size_t i = 0; i < log.records.size(); ++i) {
        const rq_error_record& r = log.records[i];
        ASSERT_EQ(r.sequence, i) << "records must arrive once each, in sequence order";
        EXPECT_GE(r.region_offset, k_session_fault_offset) << "record " << i;
        EXPECT_LT(r.region_offset, k_session_fault_offset + k_session_fault_length)
            << "record " << i << " at offset " << hex64(r.region_offset);
        EXPECT_TRUE(r.bit_mask != 0U && (r.bit_mask & (r.bit_mask - 1U)) == 0U)
            << "record " << i << " bit mask " << hex64(r.bit_mask);
        EXPECT_EQ(r.reread_outcome, RQ_REREAD_REPEATS_ACTUAL) << "record " << i;
        EXPECT_EQ(r.worker_index, 0U) << "the injected range lies in worker 0's slice";
    }

    EXPECT_EQ(log.callback_threads.size(), 1U) << "every callback must come from one thread";
    EXPECT_EQ(log.callback_threads.count(GetCurrentThreadId()), 0U)
        << "callbacks come from the coordinator, never the host thread";
    EXPECT_FALSE(probe.foreign_worker.load(std::memory_order_relaxed));
    EXPECT_FALSE(probe.changed_thread.load(std::memory_order_relaxed));
    for (std::uint32_t w = 0; w < k_session_workers; ++w) {
        const DWORD worker_thread = probe.threads[w].load(std::memory_order_relaxed);
        EXPECT_NE(worker_thread, 0U) << "worker " << w << " issued no traffic";
        EXPECT_EQ(log.callback_threads.count(worker_thread), 0U)
            << "a callback ran on worker " << w << "'s thread";
    }
}

// Stop latency takes priority over batch spacing, so a host stop may bring the run's final
// batch in early; it must be the only early one, and it must still carry every stored record
// and the true total ahead of Completed.
TEST(ErrorCollectorSession, HostStopMidStepLeavesOnlyTheFinalBatchEarlyAndAheadOfCompleted) {
    const auto clock = std::make_shared<qpc_clock>();
    session_log log(*clock);
    fault_injecting_memory_access* faults = nullptr;

    // Flips spread over the whole region, so both workers confirm errors through every
    // verification and the stop finds some still undelivered.
    rq_fault_spec spec = make_fault_spec(RQ_FAULT_SINGLE_BIT_FLIP);
    spec.bit = k_stop_flip_bit;
    spec.per_pass_count = k_session_per_pass;
    spec.max_injections = k_stop_max_injections;
    spec.seed = 0x13579BDF2468ACE0ULL;
    const char* invalid = nullptr;
    ASSERT_TRUE(validate_fault_spec(spec, k_session_region_bytes, &invalid)) << invalid;

    // fault-injecting -> counting -> real, the test DLL's stack, with the session's own clock.
    session_dependencies dependencies;
    dependencies.clock = clock;
    dependencies.wrap_memory_access =
        [&](std::unique_ptr<IMemoryAccess> real) -> std::unique_ptr<IMemoryAccess> {
        auto faulted = std::make_unique<fault_injecting_memory_access>(
            std::make_unique<counting_memory_access>(std::move(real)),
            normalized(spec, k_session_region_bytes), *clock);
        faults = faulted.get();
        return faulted;
    };

    rq_step_def step{};
    copy_utf8_truncated(step.name, sizeof(step.name), "Random sequential sweep");
    step.pattern = RQ_PATTERN_RANDOM;
    step.access_order = RQ_ORDER_SEQUENTIAL_ASCENDING;
    step.thread_mode = RQ_THREADS_ALL;
    step.hold_ms = 0U;
    step.time_budget_ms = k_stop_step_ms;
    step.coverage_budget_bp = 2'500U;

    rq_session_config config{};
    config.struct_size = sizeof(rq_session_config);
    config.step_def_size = sizeof(rq_step_def);
    config.region_bytes = k_session_region_bytes;
    config.min_region_bytes = k_session_min_region_bytes;
    config.seed = 0x2D4B6F8091A3C5E7ULL;
    config.worker_count = k_session_workers;
    config.stop_on_error = 0U;
    config.error_detail_cap = k_detail_cap;
    // Every observer tick raises progress, so the stop finds its window promptly.
    config.progress_interval_ms = k_observer_tick_ms;
    config.isa_override = RQ_ISA_AUTO;
    config.step_count = 1U;
    config.steps = &step;

    rq_callbacks callbacks{};
    callbacks.struct_size = sizeof(rq_callbacks);
    callbacks.user_data = &log;
    callbacks.on_prepared = &log_prepared;
    callbacks.on_step_starting = &log_step_starting;
    callbacks.on_step_finished = &log_step_finished;
    callbacks.on_progress = &log_progress_and_stop;
    callbacks.on_errors = &log_errors;
    callbacks.on_completed = &log_completed;
    callbacks.on_released = &log_released;

    session_handle_guard handle;
    ASSERT_EQ(session_create(&config, &callbacks, std::move(dependencies), handle.out()), RQ_OK);
    ASSERT_NE(faults, nullptr);
    {
        std::lock_guard<std::mutex> lock(log.mutex);
        log.stop_handle = handle.get();
    }
    ASSERT_EQ(session_start(handle.get()), RQ_OK);

    bool stopped_mid_step = false;
    {
        std::unique_lock<std::mutex> lock(log.mutex);
        log.changed.wait_for(lock, k_run_timeout,
                             [&log] { return log.stop_ns != 0 || log.completed; });
        stopped_mid_step = log.stop_ns != 0;
    }
    if (!stopped_mid_step) {
        // The window never came: stop anyway so the run releases, then fail.
        EXPECT_EQ(session_stop(handle.get(), RQ_STOP_USER), RQ_OK);
    }
    ASSERT_TRUE(wait_for_release(log, k_stop_timeout)) << "the session never raised on_released";
    ASSERT_TRUE(stopped_mid_step) << "no progress tick found undelivered errors after a batch";

    rq_run_result result{};
    result.struct_size = sizeof(rq_run_result);
    ASSERT_EQ(session_get_result(handle.get(), &result), RQ_OK);
    ASSERT_EQ(result.step_result_count, 1U);
    const rq_step_result step_result = result.step_results[0];
    const std::uint64_t verified_opportunities = faults->verified_opportunity_count();
    ASSERT_EQ(handle.reset(), RQ_OK);

    // No callback runs after destroy, so the log is stable from here.
    std::lock_guard<std::mutex> lock(log.mutex);
    EXPECT_FALSE(log.callback_failed.load(std::memory_order_relaxed));
    ASSERT_TRUE(log.completed);
    EXPECT_EQ(log.stop_result, RQ_OK);
    EXPECT_EQ(result.end_reason, RQ_END_USER_STOP) << result.end_detail;
    EXPECT_EQ(step_result.completed, 0U) << "the stop must land mid-step";
    EXPECT_GE(log.completed_ns, log.stop_ns) << "Completed preceded the stop request";
    const std::uint64_t latency_ns = log.completed_ns - log.stop_ns;
    EXPECT_LE(latency_ns, k_stop_bound_ns)
        << "Completed arrived "
        << static_cast<double>(latency_ns) / static_cast<double>(k_ns_per_ms)
        << " ms after the stop; the bound is 2 s";

    // Ground truth: each verified corruption is exactly one error, and the step holds them all.
    EXPECT_EQ(result.total_errors, verified_opportunities)
        << "run total " << result.total_errors << ", verified-opportunity injections "
        << verified_opportunities;
    EXPECT_EQ(step_result.errors, result.total_errors);

    const std::vector<error_call>& calls = log.error_calls;
    // The stop found errors the last batch did not carry, so at least one batch follows it.
    ASSERT_GT(calls.size(), log.batches_at_stop) << "the stop's errors were never delivered";
    std::uint64_t delivered = 0;
    std::uint64_t final_spacing_ns = 0;
    for (std::size_t i = 0; i < calls.size(); ++i) {
        const error_call& call = calls[i];
        EXPECT_FALSE(call.after_completed) << "on_errors call " << i << " followed Completed";
        if (i > 0U) {
            const error_call& previous = calls[i - 1U];
            const std::uint64_t spacing_ns = call.at_ns - previous.at_ns;
            if (i + 1U < calls.size()) {
                EXPECT_GE(spacing_ns, k_min_batch_spacing_ns)
                    << "on_errors calls " << i - 1U << " and " << i << " arrived "
                    << static_cast<double>(spacing_ns) / static_cast<double>(k_ns_per_ms)
                    << " ms apart; only the run's final batch may come early";
            } else {
                final_spacing_ns = spacing_ns;
            }
            EXPECT_GE(call.total_errors, previous.total_errors)
                << "the total fell from " << previous.total_errors << " to " << call.total_errors
                << " at call " << i;
        }
        delivered += call.count;
    }
    EXPECT_EQ(calls.back().total_errors, result.total_errors)
        << "the last batch before Completed must carry the final true total";
    const std::uint64_t stored =
        result.total_errors < k_detail_cap ? result.total_errors : std::uint64_t{k_detail_cap};
    EXPECT_EQ(delivered, stored) << "stored records delivered over " << calls.size() << " batches";
    ASSERT_EQ(log.records.size(), delivered);
    for (std::size_t i = 0; i < log.records.size(); ++i) {
        ASSERT_EQ(log.records[i].sequence, i) << "records must arrive once each, in order";
    }

    // Reported in the XML results, so the stop's batch cadence can be read without a failure.
    RecordProperty("error_batches", std::to_string(calls.size()));
    RecordProperty("final_batch_spacing_ms", std::to_string(final_spacing_ns / k_ns_per_ms));
    RecordProperty("stop_latency_ms", std::to_string(latency_ns / k_ns_per_ms));
    RecordProperty("total_errors", std::to_string(result.total_errors));
}

}  // namespace
}  // namespace rq::engine::test
