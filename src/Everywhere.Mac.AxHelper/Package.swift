// swift-tools-version: 6.0
import PackageDescription

// AxHelper: thin C-ABI shim over OpenComputerUseKit. Built as a
// dylib; .NET P/Invokes into it. Every public @_cdecl function
// MUST wrap its body in do/catch to ensure Swift / ObjC exceptions
// never propagate across the C boundary into the CoreCLR runtime
// — that's the whole point of this layer (CoreCLR's
// PAL_DispatchExceptionWrapper turns those exceptions into
// multi-second hangs on Notes / Finder).
let package = Package(
    name: "AxHelper",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "AxHelper", type: .dynamic, targets: ["AxHelper"]),
    ],
    dependencies: [
        .package(path: "../../3rd/open-codex-computer-use"),
    ],
    targets: [
        .target(
            name: "AxHelper",
            dependencies: [
                .product(name: "OpenComputerUseKit", package: "open-codex-computer-use"),
            ],
            path: "Sources/AxHelper"
        ),
        .testTarget(
            name: "AxHelperTests",
            dependencies: ["AxHelper"],
            path: "Tests/AxHelperTests"
        ),
    ]
)
