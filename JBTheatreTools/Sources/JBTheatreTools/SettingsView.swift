import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @Binding var appearance: AppAppearance
    @Environment(\.dismiss) private var dismiss
    @State private var tokenField = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Settings").font(.title2).bold()

            GroupBox(label: Label("GitHub access token", systemImage: "key.fill")) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(state.hasToken
                         ? "A token is saved in your Keychain."
                         : "No token saved — downloads are disabled until you add one.")
                        .font(.callout)
                        .foregroundStyle(state.hasToken ? Color.green : Color.secondary)

                    SecureField("Paste a fine-grained PAT…", text: $tokenField)
                        .textFieldStyle(.roundedBorder)

                    HStack {
                        Button("Save") {
                            state.setToken(tokenField)
                            tokenField = ""
                            Task { await state.refreshAll() }
                        }
                        .keyboardShortcut(.return, modifiers: [])
                        .disabled(tokenField.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                        if state.hasToken {
                            Button("Remove", role: .destructive) { state.clearToken() }
                        }
                        Spacer()
                    }

                    Link("Create a fine-grained token on GitHub →",
                         destination: URL(string: "https://github.com/settings/personal-access-tokens/new")!)
                        .font(.caption)
                    Text("Give it Contents: Read-only on your tool repos.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(8)
            }

            GroupBox(label: Label("Appearance", systemImage: "circle.lefthalf.filled")) {
                Picker("Appearance", selection: $appearance) {
                    ForEach(AppAppearance.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .padding(8)
            }

            HStack {
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 470)
    }
}
