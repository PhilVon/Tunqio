// T-143, T-164, T-167: the resources a test cannot have two of on one machine.
//
// WHY THIS EXISTS. Three stories in one day were the same defect wearing three faces: a native test went red,
// the failing assertion MOVED between runs on an unchanged binary, and the reader spent an expensive half hour
// on the audio engine or the render scale before finding a second mpcore.tests.exe. Parallel agents in separate
// worktrees produce that second copy as a matter of course, and so does a gate sweep run beside a peer's.
//
// WHAT IS ACTUALLY CONTENDED, measured on this machine by running three copies of the suite at once:
//
//   the WASAPI output device  - it is exclusive machine-wide, so the second copy loses. Two rounds of three
//     copies put the SAME cause behind five different assertions: mp_engine_play returning 5 at
//     test_output.cpp:282, mp_engine_set_output returning 2 and then 3 at :225, an unexpected MP_EVENT_DEVICE_LOST
//     at :385, output_started reading 0 at :387 - and once an enumeration that returned nothing at all, so a copy
//     reported "no output device on this machine" on a machine that has one.
//
//   the WAV fixtures in %TEMP%  - fixed by giving each process its own directory (see wav_fixture.h). Listed
//     here because it is the same family and the same tell.
//
// WHY A LOCK RATHER THAN A SKIP. A skip is the right answer when a claim is genuinely unassertable - no output
// device, no mix rate - and test_output.cpp already skips for those. But "somebody else is using the one device"
// is not unassertable, it is unassertable RIGHT NOW, and the fix for that is to take turns. Serialising keeps
// the coverage; skipping would quietly delete it on exactly the busy machines where the suite runs most.
//
// WHAT IT DELIBERATELY DOES NOT DO. It serialises mpcore.tests.exe against other mpcore.tests.exe. It cannot
// serialise the suite against a concurrent msbuild, which is the other half of T-167 - see test_quality.cpp,
// where a measurement the machine invalidated says so rather than asserting an ordering it cannot support.
#pragma once

#include <catch2/catch_amalgamated.hpp>
#include <string>

#include <windows.h>

namespace mp::tests {

// The machine-wide resources this suite has been seen to contend for. The name is the mutex name, and it is
// also what a timed-out test prints, so it is written to be read by somebody who has just seen a red run.
namespace resource {
inline constexpr const char* output_device = "the WASAPI output device";
inline constexpr const char* gpu = "the GPU (WARP rasterises on the CPU, but the D3D11 device is one machine)";
} // namespace resource

// Holds a named mutex for the lifetime of the object, so only one mpcore.tests.exe at a time runs the block.
// On timeout the test SKIPS naming the resource and the wait, rather than proceeding into an assertion whose
// failure would describe the consequence and not the cause.
//
// The wait is deliberately long: the point is to take turns, not to give up. A [quality] case measures three
// interleaved tiers at 700 ms a pin and then waits up to 20 s for the controller, so a peer's whole turn can
// legitimately run to a minute or more.
class machine_lock {
public:
    explicit machine_lock(const char* what, DWORD timeout_ms = 180000) : what_{what} {
        // A per-session name (no "Global\\"): two agents on one desktop are one session, and a session-scoped
        // mutex needs no privilege. The suffix is the resource, so the device and the GPU are separate turns.
        const std::string name = std::string{"Tunqio.mpcore.tests."} + mutex_suffix(what);
        handle_ = CreateMutexA(nullptr, FALSE, name.c_str());
        if (handle_ == nullptr) {
            // Nothing to serialise on is not a reason to fail a test; it is a reason to say so and carry on
            // exactly as the suite behaved before this header existed.
            WARN("could not create the " << what_ << " lock (error " << GetLastError()
                                         << "); running unserialised, so a concurrent mpcore.tests.exe can still "
                                            "make this case red for reasons that are not this code");
            return;
        }
        const DWORD r = WaitForSingleObject(handle_, timeout_ms);
        // WAIT_ABANDONED: a previous holder died with the mutex taken. Ownership is ours and the resource is
        // free; the state it protects is a device and a GPU, neither of which the dead process left half-written.
        if (r == WAIT_OBJECT_0 || r == WAIT_ABANDONED) {
            held_ = true;
            return;
        }
        CloseHandle(handle_);
        handle_ = nullptr;
        SKIP("another mpcore.tests.exe held " << what_ << " for the whole " << timeout_ms / 1000
                                              << " s this case waited. This is contention, not a failure: run the "
                                                 "suite once rather than twice, or wait for the other run to finish.");
    }

    ~machine_lock() {
        if (handle_ == nullptr) {
            return;
        }
        if (held_) {
            ReleaseMutex(handle_);
        }
        CloseHandle(handle_);
    }

    machine_lock(const machine_lock&) = delete;
    machine_lock& operator=(const machine_lock&) = delete;

private:
    // A mutex name may not contain a backslash, and the human-readable resource strings do not, but they do
    // contain spaces and punctuation. Reduce to the leading word run, which is unique between the two.
    static std::string mutex_suffix(const char* what) {
        std::string out;
        for (const char* p = what; *p != '\0'; ++p) {
            if ((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z')) {
                out.push_back(*p);
            } else if (!out.empty() && out.back() != '-') {
                out.push_back('-');
            }
            if (out.size() >= 24) {
                break;
            }
        }
        return out;
    }

    const char* what_;
    HANDLE handle_ = nullptr;
    bool held_ = false;
};

} // namespace mp::tests
