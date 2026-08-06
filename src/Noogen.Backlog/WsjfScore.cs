namespace Noogen.Backlog
{
    /// <summary>
    /// WSJF as SAFe defines it: cost of delay divided by job size, where cost of delay is the
    /// sum of business value, time criticality, and risk reduction / opportunity enablement.
    ///
    /// These values are computed here for the CLI's own output and for rebuilding a damaged
    /// index. On the Backlog tab the Sheet owns the live formulas — the store never writes
    /// cod/wsjf/rank there. See <see cref="BacklogPhaseExtensions.UsesLiveFormulas"/>.
    /// </summary>
    public class WsjfScore
    {
        /// <summary>Modified Fibonacci. Relative, not absolute: the smallest item in each column should be a 1.</summary>
        public static readonly IReadOnlyList<int> AllowedValues = [1, 2, 3, 5, 8, 13, 20];

        public int? BusinessValue { get; set; }

        public int? TimeCriticality { get; set; }

        public int? RiskReductionOpportunityEnablement { get; set; }

        public int? JobSize { get; set; }

        public bool IsScored =>
            BusinessValue.HasValue
            && TimeCriticality.HasValue
            && RiskReductionOpportunityEnablement.HasValue
            && JobSize.HasValue;

        public int? CostOfDelay
        {
            get
            {
                if (!BusinessValue.HasValue || !TimeCriticality.HasValue || !RiskReductionOpportunityEnablement.HasValue)
                    return null;

                return BusinessValue.Value + TimeCriticality.Value + RiskReductionOpportunityEnablement.Value;
            }
        }

        public double? Value
        {
            get
            {
                var costOfDelay = CostOfDelay;
                if (!costOfDelay.HasValue || !JobSize.HasValue || JobSize.Value == 0)
                    return null;

                return Math.Round((double)costOfDelay.Value / JobSize.Value, 2);
            }
        }

        public static void Validate(int? value, string field)
        {
            if (!value.HasValue)
                return;

            if (!AllowedValues.Contains(value.Value))
            {
                throw new ArgumentOutOfRangeException(
                    field,
                    value.Value,
                    $"{field} must be one of the modified-Fibonacci values {string.Join(", ", AllowedValues)}.");
            }
        }

        public void Validate()
        {
            Validate(BusinessValue, "bv");
            Validate(TimeCriticality, "tc");
            Validate(RiskReductionOpportunityEnablement, "rroe");
            Validate(JobSize, "size");
        }

        public WsjfScore Clone() => new()
        {
            BusinessValue = BusinessValue,
            TimeCriticality = TimeCriticality,
            RiskReductionOpportunityEnablement = RiskReductionOpportunityEnablement,
            JobSize = JobSize
        };
    }
}
