using LogForesight.Web.Auth;

namespace LogForesight.Web;

/// <summary>
/// 命令列工具：--hash-password。serverAdmin 的密碼以 PBKDF2 雜湊存放（docs/WEB-SPEC.md §6.2），
/// 這裡提供產生雜湊的方式。輪替 SOP：跑這個指令 → 把輸出貼進 appsettings.json 的
/// Auth:ServerAdmin:PasswordHash → 重啟站台。
/// </summary>
internal static class HashPasswordCommand
{
    public static int Run()
    {
        Console.Write("請輸入要雜湊的密碼：");
        var password = ReadPasswordMasked();
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("密碼不可為空。");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("請將下面這行填入 appsettings.json 的 Auth:ServerAdmin:PasswordHash：");
        Console.WriteLine();
        Console.WriteLine(PasswordHasher.Hash(password));
        Console.WriteLine();
        return 0;
    }

    /// <summary>讀取密碼但不回顯（避免密碼留在畫面與終端機的捲動紀錄裡）</summary>
    private static string ReadPasswordMasked()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }
}
