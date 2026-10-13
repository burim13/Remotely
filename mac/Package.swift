// swift-tools-version:5.9
import PackageDescription

// Remotely for macOS (Intel and Apple silicon). RemotelyKit speaks the same protocol as the Windows/.NET side, byte for byte.
let package = Package(
    name: "Remotely",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "RemotelyKit", targets: ["RemotelyKit"]),
        .executable(name: "remotely-host-mac", targets: ["RemotelyHostMac"]),
        .executable(name: "remotely-cli", targets: ["RemotelyCLI"]),
        .executable(name: "RemotelyViewerMac", targets: ["RemotelyViewerMac"]),
    ],
    targets: [
        .target(name: "RemotelyKit"),
        // Portico's design tokens and controls, shared by the viewer and the host's settings window. The font files live in mac/Fonts and are copied into the app bundles by build.sh.
        .target(name: "RemotelyUI"),
        .executableTarget(name: "RemotelyHostMac", dependencies: ["RemotelyKit", "RemotelyUI"]),
        .executableTarget(name: "RemotelyCLI", dependencies: ["RemotelyKit"]),
        .executableTarget(name: "RemotelyViewerMac", dependencies: ["RemotelyKit", "RemotelyUI"]),
        .testTarget(name: "RemotelyKitTests", dependencies: ["RemotelyKit"]),
    ]
)
