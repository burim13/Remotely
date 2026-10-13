import Foundation
import SystemConfiguration

public enum ComputerName {
    /// The name shown in System Settings > General > About, read from the system configuration store. Unlike Host.current(), it never waits on DNS.
    public static var current: String {
        if let name = SCDynamicStoreCopyComputerName(nil, nil) as String?, !name.isEmpty { return name }
        return "Mac"
    }
}
