// Catch2 entry point. Default run excludes the ASan scratch test: run `mpcore.tests ~[asan-scratch]`
// for the suite and `mpcore.tests [asan-scratch]` (expected to abort under ASan) for the check.
#include <catch2/catch_amalgamated.hpp>

int main(int argc, char* argv[]) {
    return Catch::Session().run(argc, argv);
}
