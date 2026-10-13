import XCTest
@testable import RemotelyKit

// Known-answer tests: the expected values were produced by the .NET implementation (Remotely.Core), so these prove byte-for-byte compatibility.
final class KitTests: XCTestCase {
    func hex(_ s: String) -> [UInt8] { [UInt8](hex: s)! }

    func testFieldFramingMatchesDotNet() {
        var w = FieldWriter()
        w.raw(Array("RMT1".utf8)); w.byte(1); w.field(Array("abc".utf8)); w.field([]); w.field([9, 8, 7])
        XCTAssertEqual(w.bytes.hex, "524d543101000000036162630000000000000003090807")
        var r = FieldReader(w.bytes)
        XCTAssertEqual(try r.raw(4), Array("RMT1".utf8))
        XCTAssertEqual(try r.byte(), 1)
        XCTAssertEqual(try r.field(), Array("abc".utf8))
        XCTAssertEqual(try r.field(), [])
        XCTAssertEqual(try r.field(), [9, 8, 7])
        XCTAssertTrue(r.atEnd)
        var bad = FieldReader([0, 0, 0, 9, 1])
        XCTAssertThrowsError(try bad.field())
    }

    func testAesGcmFrameFromDotNetOpens() throws {
        let key = hex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20")
        let frame = hex("00000000000000058b3bea591b894d70bd792405757c4c865685d679f2d7f915ddac96811553")
        XCTAssertEqual(frame.count, 8 + 14 + 16, "fixture is counter 5 + 14 bytes + tag")
        let channel = SecureChannel.forTesting(sendKey: key, receiveKey: key)
        // The .NET frame uses counter 5, so a fresh receiver (expecting 0) must refuse it...
        XCTAssertThrowsError(try channel.open(frame))
        // ...and a receiver that has seen counters 0-4 accepts it. Feed it five throw-away frames first.
        let sender = SecureChannel.forTesting(sendKey: key, receiveKey: key)
        let receiver = SecureChannel.forTesting(sendKey: key, receiveKey: key)
        for _ in 0..<5 { _ = try receiver.open(try sender.seal([1])) }
        XCTAssertEqual(String(decoding: try receiver.open(frame), as: UTF8.self), "hello remotely")
    }

    func testSealedFramesRoundTripAndRejectTamperingReplayAndReorder() throws {
        let key = [UInt8](repeating: 7, count: 32)
        let a = SecureChannel.forTesting(sendKey: key, receiveKey: key)
        let b = SecureChannel.forTesting(sendKey: key, receiveKey: key)
        let f0 = try a.seal(Array("zero".utf8)), f1 = try a.seal(Array("one".utf8))
        var tampered = f0; tampered[10] ^= 1
        XCTAssertThrowsError(try b.open(tampered))
        XCTAssertThrowsError(try b.open(f1))                       // reordered
        XCTAssertEqual(try b.open(f0), Array("zero".utf8))
        XCTAssertThrowsError(try b.open(f0))                       // replay
        XCTAssertEqual(try b.open(f1), Array("one".utf8))
        XCTAssertFalse(String(decoding: f0, as: UTF8.self).contains("zero"), "the plaintext must not appear in the frame")
    }

    func testHkdfMatchesDotNet() {
        let secret = hex("6465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f80818283")
        let nonceI = hex("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20")
        let nonceR = hex("2122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40")
        let transcript = hex("54e6289e14c7b0e7ad9acc2dfc4c1e3d027d0eef7f5c4c3fe7c292761d0e06a6")
        let keys = HandshakeCommon.deriveKeys(secret: secret, nonceI: nonceI, nonceR: nonceR, transcript: transcript)
        XCTAssertEqual((keys.i2r + keys.r2i).hex, "0d11104e1c11a5e507255a8190f227cd5b9659d7eeeeda5171b6024c54e73ad5425ddc1e8d48478437551c615f06aef56a6f0d4ea375f0fbfc6036badda1a126")
    }

    func testDotNetSignatureVerifiesAndIdMatches() {
        XCTAssertEqual(DeviceIds.fromPublicKey(Vectors.idSpki), Vectors.idDeviceId)
        XCTAssertTrue(DeviceIdentity.verify(publicKey: Vectors.idSpki, data: Vectors.sigData, signature: Vectors.sigValue))
        var wrong = Vectors.sigData; wrong[0] ^= 1
        XCTAssertFalse(DeviceIdentity.verify(publicKey: Vectors.idSpki, data: wrong, signature: Vectors.sigValue))
    }

    func testSwiftToSwiftHandshakeAndWrongPeerIsRefused() throws {
        let viewer = DeviceIdentity(), host = DeviceIdentity(), other = DeviceIdentity()
        let init1 = InitiatorHandshake(me: viewer, expectedResponderId: host.deviceId)
        let hello1 = try init1.createHello1()
        let accepted = try ResponderHandshake(me: host).processHello1(hello1) { $0 == viewer.deviceId }
        let viewerChannel = try init1.processHello2(accepted.hello2)
        XCTAssertEqual(accepted.initiatorId, viewer.deviceId)
        XCTAssertEqual(try accepted.channel.open(try viewerChannel.seal(Array("ping".utf8))), Array("ping".utf8))
        XCTAssertEqual(try viewerChannel.open(try accepted.channel.seal(Array("pong".utf8))), Array("pong".utf8))

        XCTAssertThrowsError(try ResponderHandshake(me: other).processHello1(hello1) { _ in true })        // hello meant for another device
        XCTAssertThrowsError(try ResponderHandshake(me: host).processHello1(hello1) { _ in false })         // untrusted viewer
        var broken = hello1; broken[broken.count / 2] ^= 1
        XCTAssertThrowsError(try ResponderHandshake(me: host).processHello1(broken) { _ in true })
        XCTAssertThrowsError(try ResponderHandshake(me: host).processHello1([1, 2, 3]) { _ in true })
    }

    func testFrameAndInputCodecs() throws {
        let frame = FrameInfo(width: 1920, height: 1080, desktop: "Winlogon", jpeg: [1, 2, 3, 4, 5])
        let back = try FrameCodec.decode(FrameCodec.encode(frame))
        XCTAssertEqual([back.width, back.height], [1920, 1080])
        XCTAssertEqual(back.desktop, "Winlogon")
        XCTAssertEqual(back.jpeg, [1, 2, 3, 4, 5])
        XCTAssertThrowsError(try FrameCodec.decode(Array(FrameCodec.encode(frame).dropLast())))
        XCTAssertThrowsError(try FrameCodec.decode([]))

        let events: [InputEvent] = [.move(100, 65535), .down(.right, 5, 6), .up(.x2, 7, 8), .scroll(-120, 1, 2), .scroll(120, 3, 4, horizontal: true),
                                    .key(down: true, virtualKey: 0x41, scanCode: 0x1E), .key(down: false, virtualKey: 0x25, extended: true), InputEvent(kind: .releaseAll)]
        XCTAssertEqual(InputCodec.decode(InputCodec.encode(events)), events)
        XCTAssertEqual(InputCodec.decode(InputCodec.encode(events))?[3].wheelDelta, -120)
        XCTAssertNil(InputCodec.decode(Array(InputCodec.encode(events).dropLast())))
        XCTAssertNil(InputCodec.decode([HostMessages.input, 0xFF, 0xFF]))
        var badKind = InputCodec.encode(events); badKind[3] = 99
        XCTAssertNil(InputCodec.decode(badKind))
        XCTAssertNil(InputCodec.decode(InputCodec.encode([InputEvent(kind: .mouseDown, arg1: 9)])))
    }

    func testControlJsonMatchesDotNetShape() throws {
        let hello = try ControlMessage.parse(Data(#"{"t":"server.hello","Challenge":"AQID","ProtocolVersion":1}"#.utf8))
        guard case .serverHello(let challenge, let version) = hello else { return XCTFail("wrong case") }
        XCTAssertEqual(challenge, [1, 2, 3]); XCTAssertEqual(version, 1)
        let list = try ControlMessage.parse(Data(#"{"t":"devices.result","Devices":[{"DeviceId":"id1","Name":"N","CanHost":true,"Online":false}]}"#.utf8))
        guard case .deviceList(let devices) = list else { return XCTFail("wrong case") }
        XCTAssertEqual(devices.first?.name, "N"); XCTAssertEqual(devices.first?.canHost, true); XCTAssertEqual(devices.first?.online, false)
        let opened = try ControlMessage.parse(Data(#"{"t":"session.opened","SessionId":"01234567-89ab-cdef-0123-456789abcdef","PeerDeviceId":"peer","IsInitiator":true}"#.utf8))
        guard case .sessionOpened(let id, let peer, let initiator) = opened else { return XCTFail("wrong case") }
        XCTAssertEqual(id.uuidString.lowercased(), "01234567-89ab-cdef-0123-456789abcdef"); XCTAssertEqual(peer, "peer"); XCTAssertTrue(initiator)
        XCTAssertThrowsError(try ControlMessage.parse(Data("[1]".utf8)))
        XCTAssertThrowsError(try ControlMessage.parse(Data(#"{"t":"nonsense"}"#.utf8)))

        // What we send: "t" must come first (the .NET deserializer requires it) and names are PascalCase.
        XCTAssertTrue(ControlBuilder.pairRequest(code: "AB-CD", name: "My \"Mac\"", canHost: true).hasPrefix(#"{"t":"pair.request","Code":"AB-CD","Name":"My \"Mac\"","CanHost":true}"#))
        XCTAssertEqual(ControlBuilder.listDevices, #"{"t":"devices.list"}"#)
    }

    func testRelayFrameGuidByteOrderMatchesDotNetBigEndian() throws {
        let id = UUID(uuidString: "01234567-89AB-CDEF-0123-456789ABCDEF")!
        let frame = RelayFrame.build(id, [0xAA])
        XCTAssertEqual(Array(frame.prefix(16)).hex, "0123456789abcdef0123456789abcdef")
        let parsed = try XCTUnwrap(RelayFrame.parse(frame))
        XCTAssertEqual(parsed.id, id); XCTAssertEqual(parsed.payload, [0xAA])
    }

    func testFileNamesCannotEscapeAndSharesRefuseTraversal() throws {
        XCTAssertEqual(FileNames.sanitize("../../etc/passwd"), "passwd")
        XCTAssertEqual(FileNames.sanitize(#"..\..\Windows\evil.exe"#), "evil.exe")
        XCTAssertEqual(FileNames.sanitize("CON.txt"), "_CON.txt")
        XCTAssertEqual(FileNames.sanitize("..."), "file")
        XCTAssertEqual(FileNames.sanitize(""), "file")
        XCTAssertEqual(FileNames.sanitize("a\u{0}b<>:|?.txt"), "ab.txt")
        XCTAssertLessThanOrEqual(FileNames.sanitize(String(repeating: "x", count: 500) + ".txt").count, 120)

        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("rmt-share-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        try Data("x".utf8).write(to: dir.appendingPathComponent("ok.txt"))
        XCTAssertNotNil(ShareDirectory.resolve(dir, name: "ok.txt"))
        XCTAssertNotNil(ShareDirectory.resolve(dir, name: "OK.TXT"))
        XCTAssertNil(ShareDirectory.resolve(dir, name: "../ok.txt"))
        XCTAssertNil(ShareDirectory.resolve(dir, name: #"..\ok.txt"#))
        XCTAssertNil(ShareDirectory.resolve(dir, name: dir.appendingPathComponent("ok.txt").path))
        XCTAssertNil(ShareDirectory.resolve(dir, name: "missing.txt"))
    }

    func testFileSinkVerifiesChecksumAndNeverOverwrites() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("rmt-sink-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: dir) }
        let data = [UInt8]((0..<1000).map { UInt8($0 % 251) })
        var hasher = SHA256Box()
        hasher.update(data)
        let good = try FileSink.create(directory: dir, requestedName: "report.bin", size: Int64(data.count), sha256: hasher.finish())
        try good.write(Array(data[0..<400])); try good.write(Array(data[400...]))
        XCTAssertEqual((try good.complete() as NSString).lastPathComponent, "report.bin")
        XCTAssertEqual(try Data(contentsOf: dir.appendingPathComponent("report.bin")), Data(data))

        let second = try FileSink.create(directory: dir, requestedName: "report.bin", size: Int64(data.count), sha256: hasher.finish())
        try second.write(data)
        XCTAssertEqual((try second.complete() as NSString).lastPathComponent, "report (1).bin")

        let bad = try FileSink.create(directory: dir, requestedName: "bad.bin", size: 5, sha256: [UInt8](repeating: 0, count: 32))
        try bad.write([1, 2, 3, 4, 5])
        XCTAssertThrowsError(try bad.complete())
        bad.abort()
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("bad.bin").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("bad.bin.part").path))
        XCTAssertThrowsError(try FileSink.create(directory: dir, requestedName: "x", size: HostMessages.maxFileBytes + 1, sha256: [UInt8](repeating: 0, count: 32)))
    }

    func testKeyMapRoundTrips() {
        XCTAssertEqual(KeyMap.macKeyCode(forVirtualKey: 0x41), 0x00)            // A
        XCTAssertEqual(KeyMap.virtualKey(forMacKeyCode: 0x00), 0x41)
        XCTAssertEqual(KeyMap.macKeyCode(forVirtualKey: 0x0D), 0x24)            // Return
        XCTAssertEqual(KeyMap.modifier(forVirtualKey: 0xA2, ctrlAsCommand: true), .command)
        XCTAssertEqual(KeyMap.modifier(forVirtualKey: 0xA2, ctrlAsCommand: false), .control)
        XCTAssertEqual(KeyMap.virtualKey(forMacModifierKeyCode: 0x37), 0xA2)    // left command -> left ctrl
        // Every mapped key round-trips.
        for vk in 0..<0x100 { if let mac = KeyMap.macKeyCode(forVirtualKey: UInt16(vk)) { XCTAssertEqual(KeyMap.virtualKey(forMacKeyCode: mac), UInt16(vk)) } }
    }

    func testInviteDecodesTheStringMadeOutsideSwiftAndRoundTrips() throws {
        // Built independently (Python) from the documented layout; the .NET test suite parses the same string.
        let known = "RMT1-AeNBHolYTMKVNOZp8flLP9e0Cr4Smw32U6t3Fji9pBqtRjRXRzRGRkp3c3M6Ly8xOTIuMTY4LjEuNTA6ODQ0My93c56b7g"
        let invite = try Invite.parse(known)
        XCTAssertEqual(invite.serverURI, "wss://192.168.1.50:8443/ws")
        XCTAssertEqual(invite.pinHex, "e3411e89584cc29534e669f1f94b3fd7b40abe129b0df653ab771638bda41aad")
        XCTAssertEqual(invite.code, "F4WG-4FFJ")
        XCTAssertEqual(try invite.encode(), known)

        // Line breaks and a lower-case prefix from copying out of an e-mail are fine.
        let messy = "  rmt1-" + String(known.dropFirst(5).prefix(20)) + "\r\n" + String(known.dropFirst(25)) + " \n"
        XCTAssertEqual(try Invite.parse(messy), invite)

        // A typing mistake, a cut-off invite and text that is not an invite are refused with plain messages.
        var typo = Array(known)
        typo[30] = typo[30] == "A" ? "B" : "A"
        XCTAssertThrowsError(try Invite.parse(String(typo))) { XCTAssertTrue("\($0)".contains("typing mistake")) }
        XCTAssertThrowsError(try Invite.parse(String(known.dropLast(6))))
        XCTAssertThrowsError(try Invite.parse("hello")) { XCTAssertTrue("\($0)".contains("RMT1-")) }

        // No TLS: no pin.
        let plain = try Invite(serverURI: "ws://127.0.0.1:19443/ws", pinHex: "", code: "ABCD-2345").encode()
        XCTAssertEqual(try Invite.parse(plain).pinHex, "")
    }
}
