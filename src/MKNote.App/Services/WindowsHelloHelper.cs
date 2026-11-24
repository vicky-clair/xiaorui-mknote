using Windows.Security.Credentials.UI;

namespace MKNote.App.Services;

/// <summary>
/// Windows Hello 生物识别 / PIN 校验助手，用于本地应用锁快速解锁。
/// </summary>
public static class WindowsHelloHelper
{
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            var availability = await UserConsentVerifier.CheckAvailabilityAsync();
            return availability == UserConsentVerifierAvailability.Available;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> RequestUnlockAsync(string message = "请验证 Windows Hello 以解锁本地 MKNote 知识库")
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(message);
            return result == UserConsentVerificationResult.Verified;
        }
        catch
        {
            return false;
        }
    }
}
