import Foundation
import UserNotifications

/// "Updates available" (and "Updated N apps") notifications through the system notification centre. macOS asks
/// the user once, the first time one is posted. Only a real .app bundle can use the notification centre — the
/// bare binary (CLI / `swift run`) has no bundle identifier and posts nothing.
enum Notifier {
    static var available: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
    }

    static func post(title: String, body: String) {
        guard available else { return }
        let center = UNUserNotificationCenter.current()
        center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
            guard granted else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }
}
