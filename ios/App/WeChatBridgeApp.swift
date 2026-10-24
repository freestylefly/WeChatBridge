import SwiftUI
import UserNotifications
import WeChatBridgeCore

@main
struct WeChatBridgeApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var store = RelayStore()
    @StateObject private var preferences = Preferences()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(store)
                .environmentObject(preferences)
        }
    }
}

/// Where a tapped 「已存入」 notification waits until the relay list can act on it.
@MainActor
final class QuickDeliverRouter: ObservableObject {
    static let shared = QuickDeliverRouter()

    struct Request: Equatable {
        let batchID: UUID
        let targetID: String?
    }

    @Published var pending: Request?

    /// A tap on the Live Activity: show this batch. Each tap is a new value,
    /// so tapping the same batch twice still navigates.
    struct Open: Equatable {
        let batchID: UUID
        let token = UUID()
    }

    @Published var open: Open?

    /// `wechatbridge://relay/<batch id>`, the Live Activity's link.
    func handle(_ url: URL) {
        guard url.scheme == "wechatbridge", url.host == "relay",
              let id = UUID(uuidString: url.lastPathComponent)
        else { return }
        open = Open(batchID: id)
    }
}

final class AppDelegate: NSObject, UIApplicationDelegate, UNUserNotificationCenterDelegate {
    func application(_ application: UIApplication, didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        // Set before launch ends, so a tap that cold-starts the app is delivered.
        UNUserNotificationCenter.current().delegate = self
        return true
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        guard let request = QuickDeliver.request(from: response) else { return }
        await MainActor.run {
            QuickDeliverRouter.shared.pending = .init(batchID: request.batchID, targetID: request.targetID)
        }
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }
}

struct RootView: View {
    @EnvironmentObject private var store: RelayStore
    @EnvironmentObject private var preferences: Preferences
    @Environment(\.scenePhase) private var scenePhase
    @State private var observer: InboxObserver?
    @State private var showsOnboarding = false
    @State private var launched = false
    @State private var tab = 0
    @ObservedObject private var router = QuickDeliverRouter.shared

    var body: some View {
        TabView(selection: $tab) {
            RelayListView(showsOnboarding: $showsOnboarding)
                .tabItem { Label("中转站", systemImage: "tray.full") }
                .tag(0)
            ScenesView()
                .tabItem { Label("场景", systemImage: "text.badge.star") }
                .tag(1)
            SettingsView(showsOnboarding: $showsOnboarding)
                .tabItem { Label("设置", systemImage: "gearshape") }
                .tag(2)
        }
        // A notification tap always lands on the relay, where the sheet opens.
        .onChange(of: router.pending) { request in
            if request != nil { tab = 0; showsOnboarding = false }
        }
        .onChange(of: router.open) { open in
            if open != nil { tab = 0; showsOnboarding = false }
        }
        .onOpenURL { router.handle($0) }
        .fullScreenCover(isPresented: $showsOnboarding) {
            OnboardingView { preferences.onboardingDone = true; showsOnboarding = false }
        }
        .onAppear(perform: launch)
        .onChange(of: scenePhase) { phase in
            switch phase {
            case .active: store.reload()
            case .background: store.commitDeletion()
            default: break
            }
        }
    }

    private func launch() {
        guard !launched else { return }
        launched = true
        DeliveryPlanner.cleanUp()
        FilesExport.placeGuide()
        observer = InboxObserver { [store] in store.reload() }
        store.reload()
        store.pruneHistory(retentionDays: preferences.retentionDays)
        if !preferences.onboardingDone && router.pending == nil { showsOnboarding = true }
        if preferences.quickDeliver {
            Task { _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) }
        }
    }
}
