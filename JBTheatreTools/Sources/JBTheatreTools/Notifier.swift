import Foundation
import UserNotifications

/// "Updates available" (and "Updated N apps") notifications through the system notification centre. Permission is
/// asked ONLY when the user switches notifications on in Settings (`requestPermission`) — posting never prompts, so
/// no system dialog can appear by itself (e.g. over the show software). Silent: alerts only, never a sound. Only a
/// real .app bundle can use the notification centre; the bare binary (CLI / `swift run`) posts nothing.
enum Notifier {
    static var available: Bool {
        Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app"
    }

    /// Asks macOS once (from the Settings toggle). `done(granted)` runs on the main queue.
    static func requestPermission(_ done: @escaping (Bool) -> Void) {
        guard available else { done(false); return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { granted, _ in
            DispatchQueue.main.async { done(granted) }
        }
    }

    static func post(title: String, body: String) {
        guard available else { return }
        let center = UNUserNotificationCenter.current()
        center.getNotificationSettings { settings in
            guard settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional else { return }
            let content = UNMutableNotificationContent()
            content.title = title
            content.body = body
            center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }
}
