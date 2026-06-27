namespace UkuuHr.Services;

/// <summary>
/// Country-specific payroll statutory configuration.
/// </summary>
public class PayrollCountryConfig
{
    public string Country { get; set; } = "Zambia";
    public string Currency { get; set; } = "ZMW";
    public double NapsaPercent { get; set; } = 5.0;
    public double NapsaCeiling { get; set; } = 9870.0;
    public double NhimaPercent { get; set; } = 1.0;
    public List<PayeBand> PayeBands { get; set; } = new();

    public static PayrollCountryConfig Default(string? country) => (country?.Trim().ToLowerInvariant()) switch
    {
        "tanzania" => Tanzania(),
        "malawi" => Malawi(),
        _ => Zambia()
    };

    public static PayrollCountryConfig Zambia() => new()
    {
        Country = "Zambia",
        Currency = "ZMW",
        NapsaPercent = 5.0,
        NapsaCeiling = 9870.0,
        NhimaPercent = 1.0,
        PayeBands = new()
        {
            new(0, 4800, 0),
            new(4800, 6900, 20),
            new(6900, 8900, 30),
            new(8900, double.MaxValue, 37.5)
        }
    };

    public static PayrollCountryConfig Tanzania() => new()
    {
        Country = "Tanzania",
        Currency = "TZS",
        NapsaPercent = 10.0, // NSSF
        NapsaCeiling = double.MaxValue,
        NhimaPercent = 4.5, // Skills Development Levy
        PayeBands = new()
        {
            new(0, 270000, 0),
            new(270000, 520000, 8),
            new(520000, 760000, 20),
            new(760000, 1000000, 25),
            new(1000000, double.MaxValue, 30)
        }
    };

    public static PayrollCountryConfig Malawi() => new()
    {
        Country = "Malawi",
        Currency = "MWK",
        NapsaPercent = 5.0,
        NapsaCeiling = double.MaxValue,
        NhimaPercent = 0,
        PayeBands = new()
        {
            new(0, 100000, 0),
            new(100000, 445000, 25),
            new(445000, 2050000, 30),
            new(2050000, double.MaxValue, 35)
        }
    };
}

public record PayeBand(double Lower, double Upper, double Rate);

/// <summary>
/// Result of a gross-to-net payroll calculation.
/// </summary>
public class PayrollCalculation
{
    public double Basic { get; set; }
    public double TaxableAllowances { get; set; }
    public double NonTaxableAllowances { get; set; }
    public double OvertimePay { get; set; }
    public double Bonuses { get; set; }

    public double Gross { get; set; }
    public double TaxableIncome { get; set; }

    public double Napsa { get; set; }
    public double Nhima { get; set; }
    public double Paye { get; set; }
    public double OtherDeductions { get; set; }

    public double TotalDeductions => Napsa + Nhima + Paye + OtherDeductions;
    public double Net => Gross - TotalDeductions;

    public double EffectivePayePercent => TaxableIncome > 0 ? Paye / TaxableIncome * 100.0 : 0;

    public List<PayeBandBreakdown> PayeBreakdown { get; set; } = new();
}

public record PayeBandBreakdown(string Label, double Lower, double Upper, double Rate, double Taxable, double Tax);

/// <summary>
/// Core payroll calculation engine — implements gross-to-net for Zambia/Tanzania/Malawi.
/// </summary>
public static class PayrollCalculator
{
    /// <summary>
    /// Compute gross-to-net payroll for one employee.
    /// </summary>
    public static PayrollCalculation Calculate(
        double basicSalary,
        List<AllowanceInput>? allowances = null,
        double overtimeHours = 0,
        double overtimeRate = 0,
        double bonuses = 0,
        double otherDeductions = 0,
        PayrollCountryConfig? countryConfig = null)
    {
        var cfg = countryConfig ?? PayrollCountryConfig.Zambia();
        allowances ??= new();

        var taxableAllow = allowances.Where(a => a.Taxable).Sum(a => a.Type == AllowanceTypeInput.Percentage
            ? basicSalary * (a.Amount / 100.0)
            : a.Amount);
        var nonTaxableAllow = allowances.Where(a => !a.Taxable).Sum(a => a.Type == AllowanceTypeInput.Percentage
            ? basicSalary * (a.Amount / 100.0)
            : a.Amount);

        var overtimePay = overtimeHours * overtimeRate;
        var gross = basicSalary + taxableAllow + nonTaxableAllow + overtimePay + bonuses;
        var taxableIncomeBase = basicSalary + taxableAllow + overtimePay + bonuses;

        // NAPSA — applied to gross, capped per country config
        var napsaRaw = gross * (cfg.NapsaPercent / 100.0);
        var napsa = Math.Min(napsaRaw, cfg.NapsaCeiling);

        // NHIMA — applied to gross
        var nhima = gross * (cfg.NhimaPercent / 100.0);

        // PAYE — progressive bands on (taxableIncomeBase - NAPSA)
        var taxableIncome = Math.Max(0, taxableIncomeBase - napsa);
        var (paye, breakdown) = ComputePaye(taxableIncome, cfg.PayeBands);

        return new PayrollCalculation
        {
            Basic = basicSalary,
            TaxableAllowances = taxableAllow,
            NonTaxableAllowances = nonTaxableAllow,
            OvertimePay = overtimePay,
            Bonuses = bonuses,
            Gross = gross,
            TaxableIncome = taxableIncome,
            Napsa = napsa,
            Nhima = nhima,
            Paye = paye,
            OtherDeductions = otherDeductions,
            PayeBreakdown = breakdown
        };
    }

    private static (double paye, List<PayeBandBreakdown> breakdown) ComputePaye(double taxableIncome, List<PayeBand> bands)
    {
        double totalTax = 0;
        var breakdown = new List<PayeBandBreakdown>();

        foreach (var band in bands)
        {
            if (taxableIncome <= band.Lower) break;
            var taxableInBand = Math.Min(taxableIncome, band.Upper) - band.Lower;
            if (taxableInBand <= 0) continue;

            var tax = taxableInBand * (band.Rate / 100.0);
            totalTax += tax;

            var upperDisplay = band.Upper == double.MaxValue
                ? "∞"
                : band.Upper.ToString("N0");

            breakdown.Add(new PayeBandBreakdown(
                Label: band.Upper == double.MaxValue
                    ? $"Beyond {band.Lower:N0}"
                    : $"{band.Lower:N0} – {upperDisplay}",
                Lower: band.Lower,
                Upper: band.Upper,
                Rate: band.Rate,
                Taxable: taxableInBand,
                Tax: tax
            ));
        }

        return (totalTax, breakdown);
    }
}

public class AllowanceInput
{
    public string Name { get; set; } = string.Empty;
    public double Amount { get; set; }
    public AllowanceTypeInput Type { get; set; } = AllowanceTypeInput.Fixed;
    public bool Taxable { get; set; } = true;
}

public enum AllowanceTypeInput { Fixed, Percentage }
