// Contract tests for the E0-S1 ABI surface: version, last-error, and the export guard.
#include "mpcore.h"

#include "abi/guard.h"
#include "abi/last_error.h"
#include "common/version.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>

TEST_CASE("mpcore_abi_version packs major and minor", "[abi]") {
    const uint32_t v = mpcore_abi_version();
    CHECK((v >> 16) == MP_ABI_MAJOR);
    CHECK((v & 0xFFFFu) == MP_ABI_MINOR);
}

TEST_CASE("mp_version is the product version from the build", "[abi]") {
    const char* v = mp_version();
    REQUIRE(v != nullptr);
    CHECK(std::string{v} == std::string{mp::version_string()});
    // major.minor.patch, digits and dots only
    CHECK(std::string{v}.find_first_not_of("0123456789.") == std::string::npos);
    CHECK(std::count(v, v + std::strlen(v), '.') == 2);
}

TEST_CASE("mp_last_error rejects a null or empty buffer", "[abi]") {
    char buf[8];
    CHECK(mp_last_error(nullptr, sizeof buf) == MP_E_INVALID_ARG);
    CHECK(mp_last_error(buf, 0) == MP_E_INVALID_ARG);
}

TEST_CASE("mp_last_error is empty when nothing failed and truncates to fit", "[abi]") {
    mp::abi::clear_last_error();
    char buf[8] = "junk";
    REQUIRE(mp_last_error(buf, sizeof buf) == MP_OK);
    CHECK(std::string{buf}.empty());

    mp::abi::set_last_error("a fairly long message");
    REQUIRE(mp_last_error(buf, sizeof buf) == MP_OK);
    CHECK(std::string{buf} == "a fairl"); // 7 chars + NUL
    mp::abi::clear_last_error();
}

TEST_CASE("last error is per thread", "[abi]") {
    mp::abi::set_last_error("main");
    std::string seen_on_worker;
    std::thread worker{[&] {
        seen_on_worker = std::string{mp::abi::last_error()};
        mp::abi::set_last_error("worker");
    }};
    worker.join();
    CHECK(seen_on_worker.empty());
    CHECK(mp::abi::last_error() == "main");
    mp::abi::clear_last_error();
}

TEST_CASE("guard converts a C++ exception to MP_E_INTERNAL with the message", "[abi]") {
    const mp_result r = mp::abi::guard([]() -> mp_result { throw std::runtime_error("boom"); });
    CHECK(r == MP_E_INTERNAL);
    CHECK(mp::abi::last_error() == "boom");
}

TEST_CASE("guard passes a normal result through and clears a stale error", "[abi]") {
    mp::abi::set_last_error("stale");
    const mp_result r = mp::abi::guard([]() -> mp_result { return MP_E_STATE; });
    CHECK(r == MP_E_STATE);
    CHECK(mp::abi::last_error().empty());
}

TEST_CASE("guard converts a structured exception to MP_E_INTERNAL", "[abi]") {
    const mp_result r = mp::abi::guard([]() -> mp_result {
        volatile int* p = nullptr;
        *p = 1; // access violation
        return MP_OK;
    });
    CHECK(r == MP_E_INTERNAL);
    CHECK(mp::abi::last_error().starts_with("structured exception 0xC0000005"));
}
