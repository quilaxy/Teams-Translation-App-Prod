using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiveTranslation.AI
{

    public static class BusinessMeetingContext
    {

        private static readonly (string Pattern, string Replacement)[] HardLocked =
        {
            (@"\bKPI\b",   "KPI"),
            (@"\bROI\b",   "ROI"),
            (@"\bSLA\b",   "SLA"),
            (@"\bVPN\b",   "VPN"),
            (@"\bISP\b",   "ISP"),
            (@"\bAPI\b",   "API"),
            (@"\bDNS\b",   "DNS"),
            (@"\bHTTP\b",  "HTTP"),
            (@"\bHTTPS\b", "HTTPS"),
            (@"\bSSL\b",   "SSL"),
            (@"\bIP\b",    "IP"),
            (@"\bURL\b",   "URL"),
            (@"\bCEO\b",   "CEO"),
            (@"\bCTO\b",   "CTO"),
            (@"\bCFO\b",   "CFO"),
            (@"\bCOO\b",   "COO"),
            (@"\bWBS\b",   "WBS"),
            (@"\bEPMT\b",  "EPMT"),
            (@"\bOKR\b",   "OKR"),
            (@"\bSOP\b",   "SOP"),
            (@"\bRACI\b",  "RACI"),
            (@"\bRFP\b",   "RFP"),
            (@"\bRFQ\b",   "RFQ"),
            (@"\bPOC\b",   "POC"),
            (@"\bUAT\b",   "UAT"),
            (@"\bSIT\b",   "SIT"),
        };

        private static readonly Regex RxQuarter = new(@"\bQ([1-4])\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RxHalfYear = new(@"\b(H[12]|Half[\s-]?Year\s*[12]?|First Half|Second Half|1st Half|2nd Half)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex RxFiscalYear = new(@"\bFY\s?(20\d{2}|\d{2})\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static readonly IReadOnlyDictionary<string, string> CommonTerms =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"AI","AI"}, {"Artificial Intelligence", "人工知能"}, {"Cloud","クラウド"}, {"Server","サーバー"}, {"System","システム"},
            {"Data","データ"}, {"Network","ネットワーク"}, {"IoT","IoT"},
            {"Database","データベース"}, {"Machine Learning","機械学習"}, {"Deep Learning","ディープラーニング"},
            {"Security","セキュリティ"}, {"Firewall","ファイアウォール"}, {"Ransomware","ランサムウェア"},
            {"Malware","マルウェア"}, {"Infrastructure","インフラ"}, {"Deployment","デプロイ"},
            {"Implementation","実装"}, {"Migration","移行"}, {"Backup","バックアップ"},
            {"Recovery","リカバリ"}, {"Monitoring","モニタリング"}, {"Maintenance","メンテナンス"},
            {"Update","アップデート"}, {"Upgrade","アップグレード"}, {"Patch","パッチ"},
            {"Version","バージョン"}, {"Release","リリース"}, {"Beta","ベータ"},
            {"Production","本番"}, {"Development","開発"}, {"Testing","テスト"},
            {"Bug","バグ"}, {"Feature","機能"}, {"Requirement","要件"}, {"Specification","仕様"},
            {"Documentation","ドキュメント"}, {"Support","サポート"}, {"License","ライセンス"},
            {"Troubleshooting","トラブルシューティング"}, {"Log","ログ"}, {"Cache","キャッシュ"},
            {"Token","トークン"}, {"Session","セッション"}, {"Authentication","認証"},
            {"Authorization","認可"}, {"Encryption","暗号化"}, {"Protocol","プロトコル"},
            {"Domain","ドメイン"}, {"Host","ホスト"}, {"Port","ポート"}, {"User","ユーザー"},
            {"Admin","管理者"}, {"Account","アカウント"}, {"Error","エラー"},
            {"Architecture","アーキテクチャ"}, {"Scalability","スケーラビリティ"},
            {"Framework","フレームワーク"},
            {"Revenue","収益"}, {"Profit","利益"}, {"Cost","コスト"}, {"Margin","マージン"},
            {"Market Share","市場シェア"}, {"Strategy","戦略"}, {"Target","目標"},
            {"Growth","成長"}, {"Risk","リスク"}, {"Customer","顧客"}, {"Investment","投資"},
            {"Efficiency","効率"}, {"Budget","予算"}, {"Deadline","締め切り"}, {"Forecast","予測"},
            {"Performance","業績"}, {"Stakeholder","ステークホルダー"}, {"Project","プロジェクト"},
            {"Resource","リソース"}, {"Vendor","ベンダー"}, {"Compliance","コンプライアンス"},
            {"Policy","ポリシー"}, {"Priority","優先度"}, {"Timeline","タイムライン"},
            {"Milestone","マイルストーン"}, {"Deliverable","成果物"}, {"Workflow","ワークフロー"},
            {"Dashboard","ダッシュボード"}, {"Report","レポート"}, {"Analytics","アナリティクス"},
            {"Metrics","メトリクス"}, {"Feedback","フィードバック"}, {"Review","レビュー"},
            {"Approval","承認"}, {"Training","トレーニング"}, {"Contract","契約"},
            {"Agreement","合意"}, {"Negotiation","交渉"}, {"Partnership","パートナーシップ"},
            {"Collaboration","コラボレーション"}, {"Communication","コミュニケーション"},
            {"Cash Flow","キャッシュフロー"}, {"Risk Management","リスク管理"},
            {"Business Continuity","事業継続"}, {"Strategic Planning","戦略計画"},
            {"Outsourcing","アウトソーシング"}, {"Merger","合併"}, {"Acquisition","買収"},
            {"Quality","品質"}, {"Process","プロセス"}, {"Cost Reduction","コスト削減"},
            {"Microsoft","Microsoft"}, {"Windows","Windows"}, {"Azure","Azure"},
            {"Teams","Teams"}, {"Outlook","Outlook"}, {"Office","Office"},
            {"Google","Google"}, {"Gmail","Gmail"}, {"Drive","Drive"},
            {"AWS","AWS"}, {"Slack","Slack"}, {"Zoom","Zoom"}, {"SAP","SAP"},
            {"Oracle","Oracle"}, {"Salesforce","Salesforce"}, {"Figma","Figma"},
            {"Jira","Jira"}, {"Confluence","Confluence"}, {"Notion","Notion"},
            {"Power BI","Power BI"}, {"Tableau","Tableau"},
            {"Half Year","半期"}, {"First Half","上半期"}, {"Second Half","下半期"},
            {"H1","上半期"}, {"H2","下半期"}, {"Q1","第1四半期"}, {"Q2","第2四半期"},
            {"Q3","第3四半期"}, {"Q4","第4四半期"}, {"Fiscal Year","会計年度"}, {"FY","会計年度"},
            {"Roadmap","ロードマップ"}, {"Workstream","ワークストリーム"}, {"Action Item","アクションアイテム"},
            {"Backlog","バックログ"}, {"Sprint","スプリント"}, {"Retrospective","レトロスペクティブ"},
            {"Stakeholder Alignment","ステークホルダー調整"}, {"Governance","ガバナンス"},
            {"EPMT","EPMT"}, {"Engineering Process Management Tools","エンジニアリングプロセス管理ツール"},
            {"WBS","WBS"}, {"Work Breakdown Structure","作業分解構成"},
            {"Deliverables","成果物"}, {"Dependencies","依存関係"}, {"Critical Path","クリティカルパス"},
            {"Risk Register","リスク登録簿"}, {"Mitigation Plan","軽減計画"},
            {"Issue Tracker","課題管理"}, {"Escalation","エスカレーション"}, {"Baseline","ベースライン"},
            {"Change Request","変更要求"}, {"Scope","スコープ"}, {"Cutover","切替"},
            {"Go-live","本番稼働"}, {"Hypercare","ハイパーケア"}, {"Postmortem","振り返り"},
            {"Runbook","運用手順書"}, {"Playbook","プレイブック"},
        };

        public enum MeetingContext
        {
            General,
            StatusUpdate,
            DecisionMaking,
            QAndA,
            Closing,
            Introduction,
        }

        public static MeetingContext Detect(string originalText)
        {
            if (string.IsNullOrWhiteSpace(originalText)) return MeetingContext.General;
            var lower = originalText.ToLowerInvariant();

            if (ContainsAny(lower, "terima kasih", "thank you", "selesai", "sampai jumpa", "closing", "wrap up"))
                return MeetingContext.Closing;
            if (ContainsAny(lower, "sepakat", "setuju", "diputuskan", "memutuskan", "approved", "decided", "agreed"))
                return MeetingContext.DecisionMaking;
            if (ContainsAny(lower, "pertanyaan", "bagaimana", "kenapa", "mengapa", "apakah", "question", "how", "why"))
                return MeetingContext.QAndA;
            if (ContainsAny(lower, "progress", "update", "laporan", "status", "perkembangan", "realisasi", "pencapaian"))
                return MeetingContext.StatusUpdate;
            if (ContainsAny(lower, "perkenalkan", "saya adalah", "tim kami", "introducing", "our team"))
                return MeetingContext.Introduction;

            return MeetingContext.General;
        }

        public static string Enrich(string translatedJP, MeetingContext context)
        {
            if (string.IsNullOrWhiteSpace(translatedJP)) return translatedJP;

            var result = translatedJP;

            foreach (var (pattern, replacement) in HardLocked)
            {
                result = Regex.Replace(result, pattern, replacement,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            result = RxQuarter.Replace(result, m => $"第{m.Groups[1].Value}四半期");
            result = RxHalfYear.Replace(result, m =>
            {
                var v = m.Value.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", "");
                if (v == "h1" || v == "firsthalf" || v == "1sthalf" || v == "halfyear1") return "上半期";
                if (v == "h2" || v == "secondhalf" || v == "2ndhalf" || v == "halfyear2") return "下半期";
                return "半期";
            });
            result = RxFiscalYear.Replace(result, m =>
            {
                var y = m.Groups[1].Value;
                if (y.Length == 2) y = $"20{y}";
                return $"{y}年度";
            });

            return result.Trim();
        }

        public static string Process(string originalText, string translatedJP)
        {
            var ctx      = Detect(originalText);
            var enriched = Enrich(translatedJP, ctx);

            if (enriched != translatedJP)
                Console.WriteLine($"[SEMANTIC-JP] [{ctx}] term consistency applied");

            return enriched;
        }

        public static string GetTermHints(int maxTerms = 12)
        {
            var priority = new[]
            {
                "EPMT", "WBS", "Q1", "Q2", "H1", "H2", "FY",
                "Machine Learning", "Deep Learning", "Data", "Security",
                "Cloud", "Server", "Network", "Database", "System",
                "Framework", "Training", "Testing", "Monitoring", "Backup",
            };

            var sb    = new System.Text.StringBuilder(" Preferred JP terms: ");
            int count = 0;
            foreach (var key in priority)
            {
                if (count >= maxTerms) break;
                if (CommonTerms.TryGetValue(key, out var val))
                {
                    sb.Append($"{key}={val}, ");
                    count++;
                }
            }

            if (sb.Length > 2 && sb[sb.Length - 2] == ',')
                sb.Length -= 2;
            sb.Append('.');

            return count > 0 ? sb.ToString() : "";
        }

        private static bool ContainsAny(string text, params string[] terms)
        {
            foreach (var t in terms)
                if (text.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}