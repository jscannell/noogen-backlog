using System.Globalization;

namespace Noogen.Backlog.Tests
{
    public class SheetTimeTests
    {
        static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

        [Fact]
        public void Epoch_is_the_lotus_compatible_1899_12_30() =>
            Assert.Equal(0, SheetTime.ToSerial(new DateTimeOffset(1899, 12, 30, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

        [Fact]
        public void Midday_is_a_half_day_past_midnight() =>
            Assert.Equal(0.5, SheetTime.ToSerial(new DateTimeOffset(1899, 12, 30, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

        [Theory]
        [InlineData("2026-08-01T08:30:00Z")]
        [InlineData("2026-01-15T23:59:00Z")]
        [InlineData("2026-12-31T00:00:00Z")]
        public void Round_trips_through_a_serial_in_every_zone(string iso)
        {
            var instant = DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

            foreach (var zone in new[] { TimeZoneInfo.Utc, NewYork, Sydney })
            {
                var round = SheetTime.FromSerial(SheetTime.ToSerial(instant, zone), zone);
                Assert.Equal(instant, round);
            }
        }

        [Fact]
        public void A_serial_is_wall_clock_in_the_configured_zone()
        {
            // 12:00Z is 08:00 in New York in August. The serial must encode the local wall clock,
            // because that is what Sheets will render.
            var instant = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

            var utcSerial = SheetTime.ToSerial(instant, TimeZoneInfo.Utc);
            var newYorkSerial = SheetTime.ToSerial(instant, NewYork);

            Assert.Equal(4.0 / 24, utcSerial - newYorkSerial, 6);
        }

        [Fact]
        public void Spring_forward_gap_shifts_instead_of_throwing()
        {
            // 02:30 on 8 March 2026 never happens in New York.
            var nonexistent = new DateTime(2026, 3, 8, 2, 30, 0);
            Assert.True(NewYork.IsInvalidTime(nonexistent));

            var resolved = SheetTime.FromWallClock(nonexistent, NewYork);

            Assert.Equal(TimeSpan.FromHours(-4), resolved.Offset);
            Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero), resolved.ToUniversalTime());
        }

        [Fact]
        public void Fall_back_ambiguity_resolves_to_the_earlier_instant()
        {
            // 01:30 on 1 Nov 2026 happens twice in New York. Picking the larger offset (-04:00)
            // means the earlier of the two, which keeps an activity log monotonic.
            var ambiguous = new DateTime(2026, 11, 1, 1, 30, 0);
            Assert.True(NewYork.IsAmbiguousTime(ambiguous));

            var resolved = SheetTime.FromWallClock(ambiguous, NewYork);

            Assert.Equal(TimeSpan.FromHours(-4), resolved.Offset);
            Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), resolved.ToUniversalTime());
        }

        [Fact]
        public void The_ambiguous_hour_is_the_one_place_a_round_trip_can_lose_an_hour()
        {
            // Honest about the cost of storing wall-clock serials: the second occurrence of the
            // repeated hour reads back as the first. Bounded at one hour, once a year, on metrics
            // reported in days.
            var secondOccurrence = new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);   // 01:30 EST

            var round = SheetTime.FromSerial(SheetTime.ToSerial(secondOccurrence, NewYork), NewYork);

            Assert.Equal(TimeSpan.FromHours(1), secondOccurrence - round);
            Assert.True(secondOccurrence - round <= TimeSpan.FromHours(1));
        }

        [Fact]
        public void Serials_sort_numerically_across_a_dst_boundary()
        {
            // The reason for real datetime cells rather than local text: sorting stays correct
            // through the repeated hour, which offset-carrying strings do not manage.
            var earlier = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);   // 01:30 EDT
            var later = new DateTimeOffset(2026, 11, 1, 7, 15, 0, TimeSpan.Zero);     // 02:15 EST

            Assert.True(SheetTime.ToSerial(earlier, NewYork) < SheetTime.ToSerial(later, NewYork));
        }

        [Theory]
        [InlineData("America/New_York")]
        [InlineData("Europe/London")]
        [InlineData("Australia/Sydney")]
        [InlineData("UTC")]
        public void Resolves_iana_ids_on_windows_linux_and_macos(string id) =>
            Assert.NotNull(SheetTime.ResolveZone(id));

        [Fact]
        public void Accepts_a_windows_id_as_a_courtesy() =>
            Assert.Equal(NewYork.BaseUtcOffset, SheetTime.ResolveZone("Eastern Standard Time").BaseUtcOffset);

        [Fact]
        public void An_empty_timezone_means_utc() => Assert.Equal(TimeZoneInfo.Utc, SheetTime.ResolveZone(null));

        [Fact]
        public void An_unknown_timezone_says_what_to_do()
        {
            var exception = Assert.Throws<InvalidOperationException>(() => SheetTime.ResolveZone("Middle/Earth"));

            Assert.Contains("IANA", exception.Message);
            Assert.Contains(BacklogSettings.TimeZoneKey, exception.Message);
        }

        [Fact]
        public void Local_zone_reports_as_an_iana_id()
        {
            // Windows would otherwise hand back "Eastern Standard Time", which Sheets rejects.
            var id = SheetTime.LocalIanaId();

            Assert.DoesNotContain("Standard Time", id);
            Assert.NotNull(SheetTime.ResolveZone(id));
        }
    }

    public class CultureSafetyTests : IDisposable
    {
        readonly CultureInfo _original = CultureInfo.CurrentCulture;

        public CultureCleanup Set(string culture)
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            return new CultureCleanup(_original);
        }

        public void Dispose() => CultureInfo.CurrentCulture = _original;

        public class CultureCleanup : IDisposable
        {
            readonly CultureInfo _restore;

            public CultureCleanup(CultureInfo restore) => _restore = restore;

            public void Dispose() => CultureInfo.CurrentCulture = _restore;
        }

        [Theory]
        [InlineData("de-DE")]   // comma decimal separator
        [InlineData("fr-FR")]
        [InlineData("en-US")]
        public void Reads_numeric_cells_the_same_in_every_culture(string culture)
        {
            using var _ = Set(culture);

            Assert.Equal(2.6, SheetTime.AsNumber(2.6));
            Assert.Equal(2.6, SheetTime.AsNumber("2.6"));
            Assert.Equal(13, SheetTime.AsNumber(13L));
            Assert.Equal(13, SheetTime.AsNumber(13));
        }

        [Fact]
        public void A_blank_or_unparseable_cell_reads_as_null()
        {
            Assert.Null(SheetTime.AsNumber(null));
            Assert.Null(SheetTime.AsNumber(string.Empty));
            Assert.Null(SheetTime.AsNumber("not a number"));
        }

        [Theory]
        [InlineData("de-DE")]
        [InlineData("en-US")]
        public async Task Cycle_time_is_identical_regardless_of_machine_culture(string culture)
        {
            using var _ = Set(culture);

            var backlog = await TestBacklog.CreateAsync(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
            var ticket = await backlog.AddAsync("Something");

            await backlog.Store.StartAsync(ticket.Id, "jason", false);
            backlog.Clock.Advance(TimeSpan.FromDays(3));
            backlog.Drive.Clock = backlog.Clock;

            var archived = await backlog.Store.ArchiveAsync(ticket.Id, Outcome.Done, null);

            Assert.Equal(3, archived.CycleDays);
        }
    }
}
