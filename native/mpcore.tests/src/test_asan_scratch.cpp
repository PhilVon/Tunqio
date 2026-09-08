// Deliberate heap-use-after-free, proving the ASan configuration catches what it is there to catch
// (E0-S1 acceptance criterion). Excluded from the default run by its tag; CI runs
//     mpcore.tests [asan-scratch]
// on the ASan build and asserts the process aborts with "heap-use-after-free" in its output.
// Never remove the tag: on a non-ASan build this is undefined behaviour with no diagnostic.
//
// Note on /sdl: the compiler's pointer sanitization rewrites the operand of `delete` to the sentinel
// 0x8123 afterwards, so reading through `block` itself would be an access violation at 0x812F, not a
// use-after-free. The read goes through an alias taken before the delete, which /sdl leaves alone.
#include <catch2/catch_amalgamated.hpp>
#include <cstdint>

namespace {

// Kept out of line so the optimiser cannot prove the read dead.
__declspec(noinline) uint32_t read_through(const uint32_t* p) {
    return *p;
}

} // namespace

TEST_CASE("asan scratch: heap use after free is detected", "[.][asan-scratch]") {
    uint32_t* block = new uint32_t[16];
    block[3] = 0xDEADBEEF;
    const uint32_t* alias = block + 3;
    delete[] block;
    const uint32_t v = read_through(alias); // use after free
    // Unreachable under ASan (the process aborts above). Without ASan, fail loudly.
    FAIL("use after free was not detected (read " << v << "); this test must run on the ASan build");
}
