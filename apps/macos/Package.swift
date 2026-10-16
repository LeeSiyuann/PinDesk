// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "PinDesk",
    platforms: [.macOS("15.2")],
    products: [.executable(name: "PinDesk", targets: ["PinDesk"])],
    targets: [
        .target(name: "PinDeskCore"),
        .executableTarget(name: "PinDesk", dependencies: ["PinDeskCore"],
                          linkerSettings: [
                            .linkedFramework("AppKit"), .linkedFramework("SwiftUI"),
                            .linkedFramework("ScreenCaptureKit"), .linkedFramework("AVFoundation"),
                            .linkedFramework("ApplicationServices"), .linkedFramework("Carbon")
                          ]),
        .testTarget(name: "PinDeskCoreTests", dependencies: ["PinDeskCore"])
    ],
    swiftLanguageVersions: [.v5]
)
