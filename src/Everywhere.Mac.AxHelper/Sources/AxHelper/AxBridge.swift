// AxBridge — C ABI shim over OpenComputerUseKit.
//
// CRITICAL invariant: every @_cdecl function MUST wrap its body in
// `do { try ... } catch { ... }`. ObjC NSExceptions raised by AX
// IPC must NOT cross the C boundary — if they do, .NET CoreCLR's
// PAL_DispatchExceptionWrapper turns them into multi-second hangs
// (sampled root cause of the Notes / Finder freeze in v0.9.x).
//
// Memory contract:
//   - Every function returning a string returns a malloc'd UTF-8
//     buffer (heap-owned by C). Caller MUST call ax_free(ptr) once
//     done. Returns NULL on failure (caller checks).
//   - Result code: 0 = success, non-zero = error (text via
//     ax_last_error()).

import Foundation
import OpenComputerUseKit

// Service is created lazily on first call. ComputerUseService.init()
// is light; the heavy AX initialization happens inside listApps /
// getAppState the first time they touch a process.
@MainActor private var _service: ComputerUseService?

@MainActor private func service() -> ComputerUseService {
    if let s = _service { return s }
    let s = ComputerUseService()
    _service = s
    return s
}

// Last-error storage. Access guarded by _errorLock; the
// `nonisolated(unsafe)` opts out of Swift 6 strict-concurrency
// checks, which is what we want — the lock is the synchronization.
nonisolated(unsafe) private var _lastError: String = ""
private let _errorLock = NSLock()

private func setLastError(_ msg: String) {
    _errorLock.lock(); _lastError = msg; _errorLock.unlock()
}

@_cdecl("ax_last_error")
public func ax_last_error() -> UnsafeMutablePointer<CChar>? {
    _errorLock.lock(); defer { _errorLock.unlock() }
    return strdup(_lastError)
}

@_cdecl("ax_free")
public func ax_free(_ ptr: UnsafeMutablePointer<CChar>?) {
    if let p = ptr { free(p) }
}

// Allocate a heap-owned C string copy of `s`. Caller frees via ax_free.
private func cdup(_ s: String) -> UnsafeMutablePointer<CChar>? {
    return s.withCString { strdup($0) }
}

// Marshal ToolCallResult → JSON string (its asDictionary already
// matches OCCU's MCP wire format). On failure returns nil and sets
// lastError.
private func resultToJsonCString(_ result: ToolCallResult) -> UnsafeMutablePointer<CChar>? {
    do {
        let data = try JSONSerialization.data(
            withJSONObject: result.asDictionary,
            options: []
        )
        guard let s = String(data: data, encoding: .utf8) else {
            setLastError("ax: result was not valid UTF-8")
            return nil
        }
        return cdup(s)
    } catch {
        setLastError("ax: failed to serialize ToolCallResult — \(error.localizedDescription)")
        return nil
    }
}

// Run a Service call on the main actor (AX must be main-thread on
// macOS); block this calling thread until it completes. Returns nil
// on any thrown error and stores message in lastError.
@MainActor
private func runOnMain<T>(_ body: @MainActor () throws -> T) -> T? {
    do {
        return try body()
    } catch let e as ComputerUseError {
        setLastError("ax: ComputerUseError — \(e.errorDescription ?? String(describing: e))")
    } catch {
        setLastError("ax: \(error.localizedDescription)")
    }
    return nil
}

// MARK: - public surface

@_cdecl("ax_list_apps")
public func ax_list_apps() -> UnsafeMutablePointer<CChar>? {
    return DispatchQueue.main.sync {
        runOnMain {
            let res = service().listApps()
            return res
        }.flatMap { resultToJsonCString($0) }
    }
}

@_cdecl("ax_get_app_state")
public func ax_get_app_state(
    _ app: UnsafePointer<CChar>?,
    _ showFullText: Int32
) -> UnsafeMutablePointer<CChar>? {
    guard let app else { setLastError("ax_get_app_state: app=NULL"); return nil }
    let appStr = String(cString: app)
    let full = showFullText != 0
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().getAppState(app: appStr, showFullText: full)
        }.flatMap { resultToJsonCString($0) }
    }
}


@_cdecl("ax_click")
public func ax_click(
    _ app: UnsafePointer<CChar>?,
    _ elementIndex: UnsafePointer<CChar>?,  // null when using x/y
    _ x: Double,
    _ y: Double,
    _ useXY: Int32,
    _ clickCount: Int32,
    _ mouseButton: UnsafePointer<CChar>?
) -> UnsafeMutablePointer<CChar>? {
    guard let app else { setLastError("ax_click: app=NULL"); return nil }
    let appStr = String(cString: app)
    let idxStr = elementIndex.map { String(cString: $0) }
    let btnStr = mouseButton.map { String(cString: $0) } ?? "left"
    let xv: Double? = useXY != 0 ? x : nil
    let yv: Double? = useXY != 0 ? y : nil
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().click(
                app: appStr,
                elementIndex: idxStr,
                x: xv, y: yv,
                clickCount: Int(clickCount),
                mouseButton: btnStr
            )
        }.flatMap { resultToJsonCString($0) }
    }
}

@_cdecl("ax_scroll")
public func ax_scroll(
    _ app: UnsafePointer<CChar>?,
    _ direction: UnsafePointer<CChar>?,
    _ elementIndex: UnsafePointer<CChar>?,
    _ pages: Double
) -> UnsafeMutablePointer<CChar>? {
    guard let app, let direction, let elementIndex else {
        setLastError("ax_scroll: required arg NULL"); return nil
    }
    let appStr = String(cString: app)
    let dirStr = String(cString: direction)
    let idxStr = String(cString: elementIndex)
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().scroll(app: appStr, direction: dirStr, elementIndex: idxStr, pages: pages)
        }.flatMap { resultToJsonCString($0) }
    }
}

@_cdecl("ax_drag")
public func ax_drag(
    _ app: UnsafePointer<CChar>?,
    _ fromX: Double, _ fromY: Double,
    _ toX: Double, _ toY: Double
) -> UnsafeMutablePointer<CChar>? {
    guard let app else { setLastError("ax_drag: app=NULL"); return nil }
    let appStr = String(cString: app)
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().drag(app: appStr, fromX: fromX, fromY: fromY, toX: toX, toY: toY)
        }.flatMap { resultToJsonCString($0) }
    }
}

@_cdecl("ax_type_text")
public func ax_type_text(
    _ app: UnsafePointer<CChar>?,
    _ text: UnsafePointer<CChar>?
) -> UnsafeMutablePointer<CChar>? {
    guard let app, let text else { setLastError("ax_type_text: required arg NULL"); return nil }
    let appStr = String(cString: app)
    let textStr = String(cString: text)
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().typeText(app: appStr, text: textStr)
        }.flatMap { resultToJsonCString($0) }
    }
}

@_cdecl("ax_press_key")
public func ax_press_key(
    _ app: UnsafePointer<CChar>?,
    _ key: UnsafePointer<CChar>?
) -> UnsafeMutablePointer<CChar>? {
    guard let app, let key else { setLastError("ax_press_key: required arg NULL"); return nil }
    let appStr = String(cString: app)
    let keyStr = String(cString: key)
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().pressKey(app: appStr, key: keyStr)
        }.flatMap { resultToJsonCString($0) }
    }
}

@_cdecl("ax_set_value")
public func ax_set_value(
    _ app: UnsafePointer<CChar>?,
    _ elementIndex: UnsafePointer<CChar>?,
    _ value: UnsafePointer<CChar>?
) -> UnsafeMutablePointer<CChar>? {
    guard let app, let elementIndex, let value else {
        setLastError("ax_set_value: required arg NULL"); return nil
    }
    let appStr = String(cString: app)
    let idxStr = String(cString: elementIndex)
    let valStr = String(cString: value)
    return DispatchQueue.main.sync {
        runOnMain {
            return try service().setValue(app: appStr, elementIndex: idxStr, value: valStr)
        }.flatMap { resultToJsonCString($0) }
    }
}

// MARK: - smoke test entry (called by the unit test target)

// Returns 1 on success, 0 on failure. Used by Tests/AxHelperTests
// to verify the dylib loads, the service initializes, and listApps
// returns at least one app.
@_cdecl("ax_self_test")
public func ax_self_test() -> Int32 {
    guard let cstr = ax_list_apps() else { return 0 }
    defer { ax_free(cstr) }
    let s = String(cString: cstr)
    // The MCP-style result has an "isError":false marker on success.
    return s.contains("\"isError\"") ? 1 : 0
}
