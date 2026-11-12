using Smena.Core.Shifts;

namespace Smena.Core.Tests.Shifts;

public sealed class ShiftRulesTests
{
    [Fact]
    public void Accepts_club_defaults()
    {
        var rules = new ShiftRules(cashFloat: 1000, maxEmployees: 3, maxTotalHours: 12, minPhotos: 3, photoMaxAgeHours: 5);

        Assert.Equal(1000, rules.CashFloat);
        Assert.Equal(3, rules.MaxEmployees);
        Assert.Equal(12, rules.MaxTotalHours);
        Assert.Equal(3, rules.MinPhotos);
        Assert.Equal(5, rules.PhotoMaxAgeHours);
    }

    [Fact]
    public void Allows_zero_float_and_zero_photos()
    {
        var rules = new ShiftRules(cashFloat: 0, maxEmployees: 1, maxTotalHours: 1, minPhotos: 0, photoMaxAgeHours: 1);

        Assert.Equal(0, rules.CashFloat);
        Assert.Equal(0, rules.MinPhotos);
    }

    [Theory]
    [InlineData(-1, 3, 12, 3, 5)]
    [InlineData(1000, 0, 12, 3, 5)]
    [InlineData(1000, 3, 0, 3, 5)]
    [InlineData(1000, 3, 12, -1, 5)]
    [InlineData(1000, 3, 12, 3, 0)]
    public void Rejects_values_out_of_range(int cashFloat, int maxEmployees, int maxTotalHours, int minPhotos, int photoMaxAgeHours)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ShiftRules(cashFloat, maxEmployees, maxTotalHours, minPhotos, photoMaxAgeHours));
    }
}
