// SPDX-License-Identifier: AGPL-3.0-only

import RemotePlay
import SwiftUI
import WebKit

/// PSN girişi. Sony'nin sayfası sistem tarayıcısında açılır; giriş bitince tarayıcının gittiği yönlendirme adresi kopyalanıp
/// buraya yapıştırılır. Eskiden giriş uygulama içi pencerede kendiliğinden açılıyordu; bazı ağlarda Sony'nin sayfası orada
/// yüklenmeyip zaman aşımına düşüyordu. O pencere yedek olarak duruyor. Uygulamaya dönülünce panodaki Sony adresi alınır.
struct PSNLoginStep: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.openURL) private var openURL
    @State private var showLogin = false
    @State private var working = false
    @State private var error: String?
    /// Pencere yönlendirmeyi yakaladı mı; yakalamadan kapanırsa kullanıcıya söylenir (eskiden sessizdi).
    @State private var redirected = false
    /// Giriş sayfasının son sorunu (açılamadı, Sony'nin HTTP hatası).
    @State private var pageProblem: String?
    @State private var showManual = false
    @State private var manualId = ""
    @State private var manualError: String?
    @State private var pasted = ""
    @State private var browserOpened = false
    @State private var copied = false
    /// Kod tek kullanımlık: aynı pano içeriği ikinci kez gönderilmez.
    @State private var lastUsed: String?

    var body: some View {
        StepLayout(title: "Sign in with the second account",
                   message: "Sign in to PlayStation Network with the second account you just created. Your password goes directly to Sony; CouchSubs only keeps the account ID.") {
            VStack(alignment: .leading, spacing: Theme.space("m")) {
                if model.accountId != nil {
                    DoneLabel(text: String(localized: "Signed in"))
                }
                HintText(text: "1. Open Sony's sign-in page in your browser and sign in with the second account.")
                HStack(spacing: Theme.space("m")) {
                    Button(model.accountId == nil ? "Open Sony sign-in in the browser" : "Sign in again in the browser", action: openBrowser)
                        .buttonStyle(CSButtonStyle(kind: model.accountId == nil ? .primary : .secondary))
                        .disabled(working)
                    Button("Copy link", action: copyLink).buttonStyle(.secondary)
                }
                if copied {
                    HintText(text: "Link copied. Paste it into a private (incognito) window if your browser is signed in to your own PSN account.")
                }
                HintText(text: "2. After signing in, a blank or redirect page opens. Copy the whole address from its address bar and paste it below.")
                TextField("", text: $pasted, prompt: Text(verbatim: PSNAccount.redirectPrefix + "?code=…"))
                    .textFieldStyle(CSTextFieldStyle())
                    .frame(maxWidth: 560)
                    .onSubmit(completePasted)
                HStack(spacing: Theme.space("m")) {
                    Button("Complete sign-in", action: completePasted)
                        .buttonStyle(.primary)
                        .disabled(working || PSNAccount.redirect(fromPasted: pasted) == nil)
                    Button("Paste") { if let text = NSPasteboard.general.string(forType: .string) { pasted = text.trimmingCharacters(in: .whitespacesAndNewlines) } }
                        .buttonStyle(.secondary)
                        .disabled(working)
                    if working { ProgressView().controlSize(.small) }
                }
                ErrorText(error: error).textSelection(.enabled)
                Button("Sign in inside the app instead") {
                    redirected = false
                    pageProblem = nil
                    showLogin = true
                }
                .buttonStyle(.secondary)
                .disabled(working)
                .padding(.top, Theme.space("s"))
                manualEntry
            }
        }
        .sheet(isPresented: $showLogin, onDismiss: sheetClosed) {
            PSNLoginSheet(onRedirect: received, onProblem: { pageProblem = $0 })
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            pickUpClipboard()
        }
    }

    /// Sony girişi açılmıyorsa (ör. servis sağlayıcı engelliyor) hesap kimliği elle yazılır.
    @ViewBuilder private var manualEntry: some View {
        if showManual {
            VStack(alignment: .leading, spacing: Theme.space("s")) {
                HintText(text: "If Sony's sign-in page does not open (some internet providers block it), enter the second account's PSN account ID in base64 (12 characters, e.g. AbCdEfGhIjk=).")
                HStack(spacing: Theme.space("m")) {
                    TextField("", text: $manualId)
                        .textFieldStyle(CSTextFieldStyle())
                        .frame(maxWidth: 240)
                        .onSubmit(saveManual)
                    Button("Save", action: saveManual).buttonStyle(.primary)
                }
                ErrorText(error: manualError)
            }
        } else {
            Button("Enter account ID manually") { showManual = true }.buttonStyle(.secondary)
        }
    }

    private func openBrowser() {
        error = nil
        browserOpened = true
        RemotePlayLog.write("PSN girişi: Sony sayfası tarayıcıda açıldı")
        openURL(PSNAccount.authorizeURL)
    }

    private func copyLink() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(PSNAccount.authorizeURL.absoluteString, forType: .string)
        browserOpened = true
        copied = true
    }

    /// Tarayıcıdan dönülünce panoda Sony'nin yönlendirme adresi varsa alanı doldurur ve girişi bitirir.
    private func pickUpClipboard() {
        guard browserOpened, !working, let text = NSPasteboard.general.string(forType: .string),
              PSNAccount.looksLikeRedirect(text), text != lastUsed else { return }
        pasted = text.trimmingCharacters(in: .whitespacesAndNewlines)
        completePasted()
    }

    private func completePasted() {
        guard !working else { return }
        guard let url = PSNAccount.redirect(fromPasted: pasted) else {
            error = String(localized: "This is not Sony's sign-in address. After signing in, copy the whole address of the page that opens (it starts with https://remoteplay.dl.playstation.net/) and paste it here.")
            return
        }
        lastUsed = pasted
        run(url, clearField: true)
    }

    private func saveManual() {
        if model.useManualAccountId(manualId) {
            manualError = nil
            error = nil
        } else {
            manualError = String(localized: "This is not a valid account ID. It is 12 characters in base64, e.g. AbCdEfGhIjk=.")
        }
    }

    /// Pencere yönlendirmesiz kapandıysa nedeni söylenir (eskiden sessizce dönülüyordu).
    private func sheetClosed() {
        guard !redirected else { return }
        let failure: Error = model.loginWindowError(problem: pageProblem)
        error = failure.localizedDescription
    }

    private func received(_ url: URL) {
        redirected = true
        showLogin = false
        run(url, clearField: false)
    }

    private func run(_ url: URL, clearField: Bool) {
        working = true
        error = nil
        Task {
            do {
                try await model.completePSNLogin(redirect: url)
                if clearField { pasted = "" }
            } catch {
                self.error = error.localizedDescription
            }
            working = false
        }
    }
}

/// Sony'nin giriş sayfasını açar; giriş bitince yönlendirme adresini yakalar.
struct PSNLoginSheet: View {
    let onRedirect: (URL) -> Void
    /// Sayfa açılamadı ya da Sony hata kodu döndürdü (nil: sorun giderildi).
    let onProblem: (String?) -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(verbatim: "PlayStation Network").font(.csHeadline)
                Spacer()
                Button("Cancel") { dismiss() }.buttonStyle(.secondary)
            }
            .padding(Theme.space("m"))
            Divider()
            LoginWebView(onRedirect: onRedirect, onProblem: onProblem)
        }
        .frame(width: 520, height: 680)
    }
}
