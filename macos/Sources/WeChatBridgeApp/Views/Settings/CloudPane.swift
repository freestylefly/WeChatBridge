import AppKit
import SwiftUI
import WeChatBridgeCore

struct CloudPane: View {
    @ObservedObject var skills: SkillLibrary
    @StateObject private var account: CloudAccount
    @State private var name = ""
    @State private var password = ""
    @State private var input = "这是一段用于验证积分扣除的模拟输入。"
    @State private var purchase: CloudRecord?
    @State private var budgetSkill: CloudRecord?
    @State private var approval: CloudRecord?

    init(skills: SkillLibrary, account: CloudAccount? = nil) {
        self.skills = skills
        _account = StateObject(wrappedValue: account ?? CloudAccount())
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            Text(L10n.text("演示技能不调用真实模型，购买和执行会消耗测试积分。"))
                .foregroundStyle(Theme.inkSecondary)
            if !account.signedIn {
                VStack(alignment: .leading, spacing: Space.m) {
                    TextField(L10n.text("服务地址（HTTPS）"), text: $account.originText)
                    TextField(L10n.text("用户名"), text: $name)
                    SecureField(L10n.text("密码"), text: $password)
                    Button(L10n.text("登录")) {
                        account.login(name: name, password: password)
                        password = ""
                    }
                }
                .textFieldStyle(.roundedBorder)
            } else {
                HStack {
                    Text(account.username)
                    Spacer()
                    Button(L10n.text("刷新")) { account.restore() }
                    Button(L10n.text("退出登录")) { account.logout() }
                }
                HStack(spacing: Space.l) {
                    metric(L10n.text("积分余额"), account.wallet.points("balance"))
                    metric(L10n.text("预留积分"), account.wallet.points("reserved"))
                    metric(L10n.text("可用积分"), account.wallet.points("available"))
                }
                Text(L10n.text("云端技能" )).font(Typo.paneBodyStrong)
                ForEach(account.catalog, id: \.selfID) { skill in
                    let pricing = skill["pricing"]?.object ?? [:]
                    HStack {
                        VStack(alignment: .leading) {
                            Text(skill.text("name"))
                            Text("\(skill.text("execution_kind")=="workflow" ? L10n.text("流程积分上限") : label(pricing.text("model"))) · \(pricing.points("points")) \(L10n.text("积分"))")
                                .font(Typo.paneCaption).foregroundStyle(Theme.inkSecondary)
                        }
                        Spacer()
                        if !skill.text("artifact_id").isEmpty {Button(L10n.text("安装完整包")){account.installPackage(skill,library:skills)}}
                        if pricing.text("model") == "buyout" {
                            if account.owned.contains(where: { $0.text("skill_id") == skill.text("id") }) {
                                Text(L10n.text("已购"))
                            } else { Button(L10n.text("买断")) { purchase = skill } }
                        } else if pricing.text("model") == "metered" {
                            Button(L10n.text("准备模拟执行")) { account.prepare(skill.text("id"), text: input) }
                            Button(L10n.text("授权 CLI 预算")) { budgetSkill = skill }
                        }
                    }.padding(.vertical, Space.s)
                }
                TextEditor(text: $input).frame(height: 90).border(Theme.inkTertiary.opacity(0.3))
                Text(L10n.text("待授权请求" )).font(Typo.paneBodyStrong)
                ForEach(account.requests, id: \.selfID) { item in
                    HStack {
                        VStack(alignment: .leading) {
                            Text("\(item.text("skill_id")) · \(item.points("points")) \(L10n.text("积分"))")
                            Text("\(item.text("device_id")) · \(label(item.text("state"))) · \(item.points("input_bytes")) bytes").font(Typo.paneCaption)
                            if item.text("execution_kind")=="workflow" {
                                Text(L10n.text("每个接口成功独立扣分，后续失败不回退已成功费用。")).font(Typo.paneCaption)
                                ForEach(item.records("steps"),id:\.selfID){step in Text("\(step.text("name")) · \(step.points("points")) \(L10n.text("积分"))").font(Typo.paneCaption)}
                            }
                        }
                        Spacer()
                        if item.text("state") == "pending" {
                            Button(L10n.text("批准消耗")) { approval = item }
                            Button(L10n.text("拒绝")) { account.decide(item.text("id"), approve: false) }
                        } else if item.text("state") == "approved", account.canResume(item.text("id")) {
                            Button(L10n.text("继续执行")) { account.decide(item.text("id"), approve: true) }
                        }
                    }.padding(.vertical, Space.s)
                }
                Text(L10n.text("执行记录" )).font(Typo.paneBodyStrong)
                ForEach(account.executions, id: \.selfID) { item in
                    HStack {
                        Text("\(label(item.text("state"))) · \(item.points("charged_points")) \(L10n.text("积分"))")
                        Spacer()
                        Button(L10n.text("查看结果")) { account.showResult(item.text("id")) }
                    }
                }
                if !account.workflowDetail.isEmpty {
                    Text(L10n.text("接口执行明细")).font(Typo.paneBodyStrong)
                    ForEach(account.workflowDetail.records("steps"),id:\.selfID){step in
                        HStack {
                            VStack(alignment:.leading){
                                Text("\(step.text("node_id")) · \(label(step.text("state")))")
                                Text("\(L10n.text("已扣积分")) \(step.points("charged_points")) · \(step.text("error_code"))\(step.text("output_error"))").font(Typo.paneCaption)
                            }
                            Spacer()
                            if step.text("state")=="failed" && step.text("provider_state")=="failed" {
                                Button(L10n.text("恢复此接口")){account.retryWorkflow(account.workflowDetail.text("id"),node:step.text("node_id"))}
                            }
                        }
                    }
                }
                Text(L10n.text("消费授权" )).font(Typo.paneBodyStrong)
                ForEach(account.grants, id: \.selfID) { grant in
                    VStack(alignment: .leading) {
                        Text("\(grant.text("skill_id")) · \(grant.points("spent_points"))/\(grant.points("limit_points")) \(L10n.text("积分"))")
                        Text(grant.text("id")).font(Typo.paneCaption).textSelection(.enabled)
                        Button(L10n.text("撤销授权")) { account.revoke(grant.text("id")) }
                    }
                }
                Text(L10n.text("积分流水" )).font(Typo.paneBodyStrong)
                ForEach(account.ledger, id: \.selfID) { item in
                    HStack { Text(item.text("kind")); Spacer(); Text("\(item.points("delta")) \(L10n.text("积分"))") }
                }
            }
            if account.busy { ProgressView() }
            if !account.notice.isEmpty { Text(account.notice).font(Typo.paneCaption).textSelection(.enabled).foregroundStyle(Theme.inkSecondary) }
        }
        .disabled(account.busy)
        .task { account.restore() }
        .onChange(of: account.catalog) { _, records in
            guard let origin = URL(string: account.originText) else { return }
            try? skills.synchronizeCloud(records, owned: account.owned, origin: origin)
        }
        .onChange(of: account.owned) { _, owned in
            guard let origin = URL(string: account.originText) else { return }
            try? skills.synchronizeCloud(account.catalog, owned: owned, origin: origin)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in account.restore() }
        .alert(L10n.text("确认积分购买"), isPresented: Binding(get: { purchase != nil }, set: { if !$0 { purchase = nil } })) {
            Button(L10n.text("取消"), role: .cancel) { purchase = nil }
            Button(L10n.text("买断")) { if let skill = purchase { account.buy(skill.text("id"), expectedPoints: skill["pricing"]?.object.points("points") ?? 0, expectedOffer: skill.text("offer_id")) }; purchase = nil }
        } message: {
            Text("\(purchase?.text("name") ?? "") · \(purchase?["pricing"]?.object.points("points") ?? 0) \(L10n.text("积分"))")
        }
        .alert(L10n.text("授权 CLI 预算"), isPresented: Binding(get: { budgetSkill != nil }, set: { if !$0 { budgetSkill = nil } })) {
            Button(L10n.text("取消"), role: .cancel) { budgetSkill = nil }
            Button(L10n.text("批准消耗")) {
                if let skill = budgetSkill { account.budget(skill: skill, points: (skill["pricing"]?.object.points("points") ?? 0) * 3, calls: 3) }
                budgetSkill = nil
            }
        } message: {
            Text(L10n.text("允许当前 CLI 在十分钟内最多执行三次，积分上限为当前单价的三倍。"))
        }
        .alert(L10n.text("确认积分授权"), isPresented: Binding(get: { approval != nil }, set: { if !$0 { approval = nil } })) {
            Button(L10n.text("取消"), role: .cancel) { approval = nil }
            Button(L10n.text("批准消耗")) { if let request = approval { account.decide(request.text("id"), approve: true) }; approval = nil }
        } message: {
            Text(approvalMessage)

        }
    }
    private var approvalMessage:String {
        guard let item=approval else{return ""}
        var text="\(item.text("skill_id")) · \(item.points("points")) \(L10n.text("积分"))\n\(item.text("device_id"))"
        if item.text("execution_kind")=="workflow" {
            text += "\n"+L10n.text("每个接口成功独立扣分，后续失败不回退已成功费用。")
            for step in item.records("steps"){text += "\n\(step.text("name")) · \(step.points("points")) \(L10n.text("积分"))"}
        }
        return text
    }
    private func label(_ value: String) -> String {
        switch value {
        case "buyout": L10n.text("买断")
        case "metered": L10n.text("按量计费")
        case "free": L10n.text("免费")
        case "accepted": L10n.text("待执行")
        case "running": L10n.text("执行中")
        case "succeeded": L10n.text("已完成")
        case "failed": L10n.text("失败")
        case "partial": L10n.text("部分完成")
        case "reconciling": L10n.text("待核实")
        case "cancelled": L10n.text("已取消")
        case "pending": L10n.text("待批准")
        case "approved": L10n.text("已批准")
        case "rejected": L10n.text("已拒绝")
        case "expired": L10n.text("已过期")
        default: value
        }
    }
    private func metric(_ key: String, _ value: Int64) -> some View {
        VStack(alignment: .leading) { Text(key).font(Typo.paneCaption); Text("\(value)").font(.title2.monospacedDigit()) }
    }
}
private extension Dictionary where Key == String, Value == CloudValue {
    var selfID: String { text("id") }
}
