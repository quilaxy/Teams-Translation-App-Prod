using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveTranslation.AI
{
    // Optimasi semantic-jp:
    // - Regex di-compile SEKALI (versi lama: 26x Regex.Replace statis per panggilan; cache regex .NET
    //   hanya 15 pola, jadi pola terus dikompilasi ulang).
    // - Batas kata memakai lookaround ASCII, bukan \b: di teks Jepang "APIを" / "Q1の" TIDAK dianggap
    //   berbatas oleh \b (huruf Jepang termasuk \w), sehingga versi lama gagal mencocokkan.
    // - Term hint untuk prompt kini DINAMIS: hanya istilah yang benar-benar ada di teks sumber, dan tanpa
    //   pasangan identitas (EPMT=EPMT). Kebanyakan panggilan tidak menambah token sama sekali.
    // - Pasca-proses (Process) sekarang dipanggil dari AITranslationEngine untuk target Jepang.
    public static class BusinessMeetingContext
    {
        private const string Lb = @"(?<![A-Za-z0-9])";
        private const string Rb = @"(?![A-Za-z0-9])";
        private const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

        // Akronim yang harus tetap kapital. ("SIT" dibuang: kata Inggris "sit" ikut tertimpa bila IgnoreCase.)
        private static readonly Regex RxAcronym = new(
            Lb + "(KPI|ROI|SLA|VPN|ISP|API|DNS|HTTPS?|SSL|IP|URL|CEO|CTO|CFO|COO|WBS|EPMT|OKR|SOP|RACI|RFP|RFQ|POC|UAT)" + Rb, Opt);
        private static readonly Regex RxQuarter    = new(Lb + "Q([1-4])" + Rb, Opt & ~RegexOptions.IgnoreCase);
        private static readonly Regex RxHalfYear   = new(Lb + @"(H[12]|Half[\s-]?Year\s*[12]?|First Half|Second Half|1st Half|2nd Half)" + Rb, Opt);
        private static readonly Regex RxFiscalYear = new(Lb + @"FY\s?(20\d{2}|\d{2})" + Rb, Opt);

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

        // Hanya istilah yang BERUBAH saat diterjemahkan (identitas seperti EPMT=EPMT, Zoom=Zoom dibuang).
        // RxGlossaryLoose: memindai teks SUMBER (untuk hint & penentuan istilah yang muncul).
        // RxGlossary: dipakai pada KELUARAN; tidak mengganti istilah yang diapit kata Latin lain
        // (mis. "Azure Update Manager") supaya nama produk tidak terpecah.
        private static readonly string GlossaryKeys = string.Join("|",
            CommonTerms
                .Where(kv => !string.Equals(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .OrderByDescending(k => k.Length)
                .Select(Regex.Escape));

        private static readonly Regex RxGlossaryLoose = new(Lb + "(" + GlossaryKeys + ")" + Rb, Opt);
        private static readonly Regex RxGlossary = new(
            @"(?<![A-Za-z0-9][ -])" + Lb + "(" + GlossaryKeys + ")" + Rb + @"(?![ -][A-Za-z0-9])", Opt);

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

        // Pasca-proses terjemahan Jepang: akronim, kuartal/semester/tahun fiskal, dan istilah glosarium
        // yang MUNCUL di sumber tetapi dibiarkan berbahasa Latin oleh model.
        public static string Enrich(string translatedJP, MeetingContext context)
            => Enrich(translatedJP, null);

        public static string Enrich(string translatedJP, string? sourceText)
        {
            if (string.IsNullOrWhiteSpace(translatedJP)) return translatedJP;

            var result = RxAcronym.Replace(translatedJP, m => m.Value.ToUpperInvariant());
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

            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                var present = new HashSet<string>(
                    RxGlossaryLoose.Matches(sourceText).Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
                if (present.Count > 0)
                    result = RxGlossary.Replace(result, m =>
                        present.Contains(m.Value) && CommonTerms.TryGetValue(m.Value, out var jp) ? jp : m.Value);
            }

            return result.Trim();
        }

        public static string Process(string originalText, string translatedJP)
        {
            var enriched = Enrich(translatedJP, originalText);

            if (enriched != translatedJP)
                Console.WriteLine("[SEMANTIC-JP] term consistency applied");

            return enriched;
        }

        // Hint untuk system prompt: hanya istilah yang ada di teks sumber (maks. maxTerms).
        // Mengembalikan "" bila tidak ada, sehingga tidak ada token tambahan.
        public static string GetTermHints(string? sourceText, int maxTerms = 8)
        {
            if (string.IsNullOrWhiteSpace(sourceText)) return "";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb   = new StringBuilder();
            foreach (Match m in RxGlossaryLoose.Matches(sourceText))
            {
                if (seen.Count >= maxTerms) break;
                if (!seen.Add(m.Value)) continue;
                if (!CommonTerms.TryGetValue(m.Value, out var jp)) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(m.Value).Append('=').Append(jp);
            }

            return sb.Length == 0 ? "" : " JP terms: " + sb + ".";
        }

        private static bool ContainsAny(string text, params string[] terms)
        {
            foreach (var t in terms)
                if (text.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}