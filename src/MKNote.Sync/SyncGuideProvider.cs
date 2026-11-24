namespace MKNote.Sync;

/// <summary>
/// 内置各同步方式的三语（简体中文 / 英文 / 日文）分步配置指南。
/// </summary>
public static class SyncGuideProvider
{
    public static string GetGuide(string provider, string lang = "zh-CN") => (provider, lang) switch
    {
        ("SynologyWebDav", "en-US") => """
            【Synology NAS (WebDAV) Setup Guide — Recommended for LAN & Remote Access】
            1. Open Synology DSM -> Package Center -> Install & launch "WebDAV Server".
            2. In WebDAV Server settings, enable HTTPS (default port 5006) or HTTP (port 5005, LAN only).
            3. Create a shared folder or subfolder on your NAS (e.g. /homes/yourname/MKNoteSync) and grant your DSM user Read/Write permissions.
            4. Enter Server URL here, e.g. https://192.168.1.100:5006 or https://your-nas.example:5006.
            5. Enter Remote Path (e.g. /homes/yourname/MKNoteSync), DSM Username & Password.
            6. Keep "End-to-End Encryption (E2EE)" ON and set a strong Master Passphrase (same across all devices). All notes & images are encrypted with AES-256-GCM before leaving your PC.
            """,
        ("SynologyWebDav", "ja-JP") => """
            【Synology NAS (WebDAV) 設定・使用方法（LAN / 外出先両対応・推奨）】
            1. Synology DSM の「パッケージセンター」から「WebDAV Server」をインストールして起動します。
            2. WebDAV Server で HTTPS（既定ポート 5006）または LAN 内 HTTP（5005）を有効化します。
            3. NAS 側に同期用フォルダ（例: /homes/ユーザー名/MKNoteSync）を作成し、読み書き権限を付与します。
            4. サーバーURLに https://192.168.1.100:5006 などを入力し、リモートパス・DSMアカウントを入力します。
            5. 「エンドツーエンド暗号化 (E2EE)」をオンにして同期用マスターパスワードを設定します（全端末共通）。すべてのノートと画像は送信前に AES-256-GCM で暗号化されます。
            """,
        ("SynologyWebDav", _) => """
            【群晖 Synology NAS（WebDAV 加密同步）使用方法 —— 推荐内网/外网通用】
            第 1 步：登录群晖 DSM 后台 → 打开「套件中心」→ 搜索并安装「WebDAV Server」。
            第 2 步：打开 WebDAV Server，勾选启用 HTTPS（默认端口 5006；纯局域网也可用 HTTP 5005）。
            第 3 步：在文件管理中新建一个专用同步目录（例如 /homes/你的用户名/MKNoteSync），确保该 DSM 账号对其拥有「读写」权限，并在「控制面板 → 应用程序权限」中允许该账号使用 WebDAV Server。
            第 4 步：在上方「服务器地址」填写 https://你的群晖IP:5006（或公网 DDNS 域名）；「远端路径」填写 /homes/你的用户名/MKNoteSync。
            第 5 步：若局域网使用自签名证书，请保持勾选「允许群晖 NAS 局域网自签名 HTTPS 证书」。
            第 6 步：强烈建议开启「端到端加密 (E2EE)」并设置加密主密码（多设备需一致）。所有笔记、截图和图片都会在离开电脑前完成 AES-256-GCM 加密，远端仅存放密文。
            """,

        ("SynologySmb", "en-US") => """
            【Synology NAS (SMB / LAN Shared Folder) Setup Guide — Ultra-fast LAN Sync】
            1. On Synology DSM: Control Panel -> File Services -> Enable SMB Service.
            2. Create a shared folder (e.g. MKNoteSync) and access it via UNC path (e.g. \\192.168.1.100\MKNoteSync) or map it to a drive letter.
            3. Ensure Windows has saved credentials for the NAS share (open the path once in File Explorer and check "Remember my credentials").
            4. Paste the UNC path (\\NAS-IP\Share\MKNoteSync) or mapped drive letter (Z:\MKNoteSync) into Path above.
            5. Keep E2EE enabled so every file written to the NAS share is strictly AES-256-GCM encrypted.
            """,
        ("SynologySmb", "ja-JP") => """
            【Synology NAS (SMB 共有フォルダ) 設定・使用方法（高速 LAN 同期）】
            1. DSM の「コントロールパネル → ファイルサービス」で SMB サービスを有効にします。
            2. 共有フォルダを作成し、Windows エクスプローラーで \\192.168.1.100\MKNoteSync を開いて資格情報を保存します（またはネットワークドライブに割り当てます）。
            3. 上のパス欄に \\192.168.1.100\MKNoteSync または Z:\MKNoteSync を入力します。
            4. E2EE を有効にすると、NAS 上には AES-256-GCM 暗号化データのみが保存されます。
            """,
        ("SynologySmb", _) => """
            【群晖 Synology NAS（SMB 局域网共享直连）使用方法 —— 局域网极速同步】
            第 1 步：在群晖 DSM「控制面板 → 文件服务 → SMB」中确认已启用 SMB 服务。
            第 2 步：在群晖创建共享文件夹（如 MKNoteSync），在 Windows 文件资源管理器地址栏输入 \\你的群晖IP\MKNoteSync，登录并勾选「记住我的凭据」（或映射为网络驱动器如 Z:\MKNoteSync）。
            第 3 步：在上方「同步目录路径」填入 \\192.168.1.100\MKNoteSync（或 Z:\MKNoteSync）。
            第 4 步：开启「端到端加密 (E2EE)」并设置加密主密码，点击「测试连接」→「立即加密同步」即可完成局域网高速加密同步。
            """,

        ("GenericWebDav", "en-US") => """
            【Generic WebDAV Server Setup Guide】
            1. Enable WebDAV on your server or cloud storage and obtain the WebDAV endpoint URL & App-Specific Password.
               - Example: https://your-webdav-server.example/dav/
            2. Enter Server URL, Remote Path (e.g. /MKNoteSync), Username, and App Password above.
            3. Enable E2EE so the remote server only stores AES-256-GCM encrypted ciphertext.
            """,
        ("GenericWebDav", "ja-JP") => """
            【汎用 WebDAV サーバー設定・使用方法】
            1. ご利用のサーバーまたはストレージで WebDAV を有効化し、URL と専用パスワードを取得します。
            2. サーバーURL、リモートパス（例: /MKNoteSync）、ユーザー名、パスワードを入力します。
            3. E2EE をオンにすることで、サーバー側には AES-256-GCM 暗号化データのみが保存されます。
            """,
        ("GenericWebDav", _) => """
            【通用 WebDAV 服务器 / 云盘 使用方法】
            第 1 步：在支持标准 WebDAV 协议的服务器或网盘后台启用 WebDAV，并获取服务器地址与专用访问密码（例如 https://your-webdav-server.example/dav）。
            第 2 步：在上方填写服务器地址、远端同步子目录（如 /MKNoteSync）、账号与专用密码。
            第 3 步：务必开启「端到端加密 (E2EE)」并设定主密码，远端仅存储 AES-256-GCM 密文，零明文泄露风险。
            """,

        ("LocalFolder", "en-US") => """
            【Local / External Drive Folder Sync Setup Guide】
            1. Choose any local sync directory, external USB drive, or removable storage folder.
            2. Keep E2EE ON: MKNote Next writes only AES-256-GCM encrypted files (.mke / .mkb) into that folder.
            3. Point MKNote Next on another PC to the same folder with the same E2EE passphrase to sync seamlessly.
            """,
        ("LocalFolder", "ja-JP") => """
            【ローカル / 外部ストレージ同期フォルダ使用方法】
            1. 任意のローカル同期フォルダ、または USB ドライブ・外付けストレージのディレクトリを指定します。
            2. E2EE をオンにしておけば、暗号化済みファイル（.mke / .mkb）のみが書き出されます。
            3. 別の PC でも同じフォルダと E2EE パスワードを設定するだけで安全に同期できます。
            """,
        _ => """
            【本地目录 / 移动存储同步目录 使用方法】
            第 1 步：点击「浏览...」选择任意本地同步目录、U 盘或移动硬盘中的专用文件夹（例如 D:\MKNoteSync）。
            第 2 步：保持开启「端到端加密 (E2EE)」并设置加密主密码，本应用仅向该目录写入 AES-256-GCM 加密后的密文块（.mke / .mkb）。
            第 3 步：在另一台电脑中指向同一目录并输入相同 E2EE 主密码即可双向安全同步（双端并发修改时会自动保留「同步冲突副本」，永不丢失正文）。
            """
    };
}
