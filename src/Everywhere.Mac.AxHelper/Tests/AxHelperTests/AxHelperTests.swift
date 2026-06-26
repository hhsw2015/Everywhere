// Unit tests for the C-ABI bridge. Each test exercises one
// @_cdecl entry point and verifies:
//   1. it returns a non-NULL JSON string (or NULL with a meaningful
//      lastError, never a crash);
//   2. memory is owned by C and can be ax_free'd;
//   3. the JSON shape matches OCCU's MCP wire format.

import Foundation
import Testing
@testable import AxHelper

@Suite("AxHelper C-ABI bridge")
struct AxHelperTests {

    /// Convert a `char*` returned from the bridge into a Swift String,
    /// then ax_free the C buffer. Returns nil on NULL.
    func consumeCString(_ ptr: UnsafeMutablePointer<CChar>?) -> String? {
        guard let p = ptr else { return nil }
        let s = String(cString: p)
        ax_free(p)
        return s
    }

    @Test func selfTestRunsWithoutCrashing() {
        // We accept either outcome:
        //   1 = a11y granted, listApps succeeded (interactive dev box)
        //   0 = a11y denied (CI runners), listApps returned isError
        // The point is the dylib loads + the ObjC @try/@catch shim
        // never lets an exception bubble out as a crash.
        let rc = ax_self_test()
        #expect(rc == 0 || rc == 1, "ax_self_test must return 0 or 1, got \(rc)")
    }

    @Test func listAppsReturnsValidJson() {
        guard let json = consumeCString(ax_list_apps()) else {
            #expect(Bool(false), "ax_list_apps returned NULL: \(consumeCString(ax_last_error()) ?? "no error")")
            return
        }
        let data = json.data(using: .utf8)!
        let parsed = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        #expect(parsed != nil, "result was not valid JSON")
        #expect(parsed?["content"] is [Any], "missing 'content' array in MCP shape")
        #expect(parsed?["isError"] as? Bool == false, "listApps should not error on a normal host")
    }

    @Test func nullArgumentsAreRejectedNotCrashed() {
        // Each function with a required app argument should return nil
        // when given NULL (and set lastError) rather than crashing.
        #expect(ax_get_app_state(nil, 0) == nil)
        let err1 = consumeCString(ax_last_error())
        #expect(err1?.contains("app=NULL") == true)

        #expect(ax_click(nil, nil, 0, 0, 0, 1, nil) == nil)
        let err2 = consumeCString(ax_last_error())
        #expect(err2?.contains("app=NULL") == true)

        #expect(ax_type_text(nil, nil) == nil)
        let typeErr = consumeCString(ax_last_error())
        #expect(typeErr?.contains("NULL") == true)

        #expect(ax_press_key(nil, nil) == nil)
        let pressErr = consumeCString(ax_last_error())
        #expect(pressErr?.contains("NULL") == true)

        #expect(ax_set_value(nil, nil, nil) == nil)
        let setErr = consumeCString(ax_last_error())
        #expect(setErr?.contains("NULL") == true)

        #expect(ax_scroll(nil, nil, nil, 1.0) == nil)
        let scrollErr = consumeCString(ax_last_error())
        #expect(scrollErr?.contains("NULL") == true)

        #expect(ax_drag(nil, 0, 0, 0, 0) == nil)
        let dragErr = consumeCString(ax_last_error())
        #expect(dragErr?.contains("NULL") == true)
    }

    @Test func unknownAppReturnsErrorNotCrash() {
        // OCCU's getAppState should throw `appNotFound` for an
        // app that doesn't exist; the bridge converts it into
        // a NULL return + last_error message.
        let bogus = "this-app-does-not-exist-anywhere-9b3f2c1e"
        let p = bogus.withCString { ax_get_app_state($0, 0) }
        // OCCU may throw OR return an isError result; both are
        // acceptable as "no crash". Just verify we got back a
        // determinate value.
        if let json = consumeCString(p) {
            #expect(!json.isEmpty)
        } else {
            let err = consumeCString(ax_last_error())
            #expect(err?.isEmpty == false)
        }
    }

    @Test func freeNullIsSafe() {
        ax_free(nil) // must not crash
    }
}
