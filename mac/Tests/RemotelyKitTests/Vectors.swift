import Foundation
import CryptoKit
@testable import RemotelyKit

/// Produced by the .NET implementation (see the repository's vector generator notes in docs/architecture.md). Do not edit by hand.
enum Vectors {
    static let idSpki = [UInt8](hex: "3059301306072a8648ce3d020106082a8648ce3d03010703420004a4d9443c67206ecbff9d80e913f3807224886e9e790ee3937afa0d5cc5af8eb2d3a5a5e9b08446b539e9d731cf37db4bbef801295217b538096e9bca13821f67")!
    static let idDeviceId = "7bb77076c81fbb872bad4b13225a6c8b4acfb78b8dafec8e8fd51ed64cbdf0b0"
    static let sigData = [UInt8](hex: "7369676e656420627920646f746e6574")!
    static let sigValue = [UInt8](hex: "d40540424934a1d9af8aed3458beaf91e61190e30930331089cab53201a3482f37a5ffbd8a97bb9f28338b4cd3ab631e537a60a2c7658e26d2ad2268e96c7f89")!
}

/// SHA-256 helper for tests.
struct SHA256Box {
    private var inner = SHA256()
    mutating func update(_ b: [UInt8]) { inner.update(data: b) }
    func finish() -> [UInt8] { Array(inner.finalize()) }
}
