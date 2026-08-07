namespace Noogen.Backlog.Tests
{
    public class WsjfScoreTests
    {
        [Fact]
        public void Value_EveryDimensionScored_DividesCostOfDelayByJobSize()
        {
            var score = new WsjfScore
            {
                BusinessValue = 8,
                TimeCriticality = 3,
                RiskReductionOpportunityEnablement = 2,
                JobSize = 5
            };

            Assert.Equal(13, score.CostOfDelay);
            Assert.Equal(2.6, score.Value);
            Assert.True(score.IsScored);
        }

        [Fact]
        public void Value_DivisionIsNotExact_RoundsToTwoDecimals()
        {
            var score = new WsjfScore
            {
                BusinessValue = 1,
                TimeCriticality = 1,
                RiskReductionOpportunityEnablement = 1,
                JobSize = 13
            };

            Assert.Equal(0.23, score.Value);
        }

        [Fact]
        public void Value_ADimensionIsUnscored_IsNull()
        {
            var score = new WsjfScore { BusinessValue = 8, TimeCriticality = 3 };

            Assert.False(score.IsScored);
            Assert.Null(score.Value);
            Assert.Null(score.CostOfDelay);
        }

        [Fact]
        public void Value_JobSizeIsZero_IsNullRatherThanDividingByZero()
        {
            var score = new WsjfScore
            {
                BusinessValue = 8,
                TimeCriticality = 3,
                RiskReductionOpportunityEnablement = 2,
                JobSize = 0
            };

            Assert.Null(score.Value);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(8)]
        [InlineData(13)]
        [InlineData(20)]
        public void Validate_ValueIsOnTheModifiedFibonacciScale_Accepts(int value) => WsjfScore.Validate(value, "bv");

        [Theory]
        [InlineData(4)]
        [InlineData(0)]
        [InlineData(21)]
        [InlineData(-1)]
        public void Validate_ValueIsOffTheScale_Throws(int value) =>
            Assert.Throws<ArgumentOutOfRangeException>(() => WsjfScore.Validate(value, "bv"));

        [Fact]
        public void Validate_ValueIsNull_AcceptsBecauseAnUnscoredItemIsLegitimate() => WsjfScore.Validate(null, "bv");
    }
}
