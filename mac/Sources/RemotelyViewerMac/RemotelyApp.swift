import SwiftUI
import RemotelyUI
import AppKit

@main
struct RemotelyApp: App {
    @StateObject private var model = AppModel()

    init() { Theme.registerFonts() }

    var body: some Scene {
        WindowGroup("Remotely") {
            MainView(app: model)
        }
        .windowResizability(.contentMinSize)
        .defaultSize(width: 440, height: 640)
    }
}
