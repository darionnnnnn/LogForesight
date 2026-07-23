using System.ComponentModel.DataAnnotations;

namespace LogForesight.Web.Models.Dto;

public class LoginRequest
{
    [Required(ErrorMessage = "請輸入帳號")]
    public string Account { get; set; } = string.Empty;

    /// <summary>Stub 模式後端一律通過、不比對此欄位值（登入頁仍照常顯示密碼欄，只是設為選填）</summary>
    public string? Password { get; set; }
}

/// <summary>登入成功後回給前端的身分資訊（側欄選單與功能鈕的顯示依據）</summary>
public class CurrentUserDto
{
    public long UserId { get; set; }

    public string Account { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsServerAdmin { get; set; }

    /// <summary>能力字串陣列。**前端只用來決定顯示什麼，真正的防線在後端**</summary>
    public List<string> Capabilities { get; set; } = new();

    /// <summary>尚無任何 admin 成員時為 true，前端據此提示 serverAdmin 去指派</summary>
    public bool NeedsAdminSetup { get; set; }
}

/// <summary>登入頁初始化資訊（是否需要密碼欄）</summary>
public class LoginOptionsDto
{
    public string Provider { get; set; } = string.Empty;

    public bool RequiresPassword { get; set; }
}
