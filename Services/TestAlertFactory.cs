using NekoNoTe.Models;

namespace NekoNoTe.Services;

public static class TestAlertFactory
{
    public static AlertInfo CreateGreatEastJapanEew(int arrivalSeconds = 15)
    {
        return new AlertInfo
        {
        Id = $"test-eew:{Guid.NewGuid()}",
        Kind = AlertKind.EewWarning,
        Title = "【テスト】緊急地震速報（東日本大震災相当）",
        Body = $"震源地: 三陸沖\n予想最大震度: 7　マグニチュード: 9.0\n主要動到達予想: {arrivalSeconds}秒後（テスト）",
        ReportedAt = DateTimeOffset.Now,
        IsCritical = true,
        ShouldNotify = false,
        Detail = new EarthquakeDetail
        {
            HypocenterName = "三陸沖",
            Magnitude = 9.0,
            Depth = 24,
            Latitude = 38.1035,
            Longitude = 142.861,
            OriginTime = DateTimeOffset.Now.AddSeconds(-5),
            ArrivalTime = DateTimeOffset.Now.AddSeconds(arrivalSeconds),
            Points = new[]
            {
                new EarthquakeIntensityPoint("宮城県", "栗原市", 70, "7"),
                new EarthquakeIntensityPoint("福島県", "浜通り", 60, "6強"),
                new EarthquakeIntensityPoint("茨城県", "県北部", 60, "6強"),
                new EarthquakeIntensityPoint("栃木県", "県北部", 60, "6強"),
                new EarthquakeIntensityPoint("岩手県", "沿岸南部", 55, "6弱"),
            },
        },
        };
    }
}
