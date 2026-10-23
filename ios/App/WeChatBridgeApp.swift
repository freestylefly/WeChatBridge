import SwiftUI
import WeChatBridgeCore

@main
struct WeChatBridgeApp: App {
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

struct RootView: View {
    @EnvironmentObject private var store: RelayStore
    @EnvironmentObject private var preferences: Preferences
    @Environment(\.scenePhase) private var scenePhase
    @State private var observer: InboxObserver?
    @State private var showsOnboarding = false
    @State private var launched = false

    var body: some View {
        TabView {
            RelayListView(showsOnboarding: $showsOnboarding)
                .tabItem { Label("中转站", systemImage: "tray.full") }
            ScenesView()
                .tabItem { Label("场景", systemImage: "text.badge.star") }
            SettingsView(showsOnboarding: $showsOnboarding)
                .tabItem { Label("设置", systemImage: "gearshape") }
        }
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
        observer = InboxObserver { [store] in store.reload() }
        store.reload()
        store.pruneHistory(retentionDays: preferences.retentionDays)
        if !preferences.onboardingDone { showsOnboarding = true }
    }
}
