using static Mailvec.DevCorpus.Mime;

namespace Mailvec.DevCorpus;

/// <summary>
/// Background mail: enough ordinary messages across topics, folders, senders
/// and two and a half years that search has something to rank against,
/// pagination has pages and KNN escalation has distractors. Seeded, so the
/// same count is the same bytes every run (a seeded System.Random uses the
/// legacy algorithm, which .NET keeps stable across versions).
/// </summary>
internal static class Filler
{
    private static readonly string[] Shops = ["Harbor Hardware", "Greenleaf Garden Centre", "Corner Bakery", "Northwind Books", "Metro Pharmacy"];
    private static readonly string[] Items = ["garden twine", "seed trays", "sourdough loaf", "paperback novel", "plasters", "watering can", "pruning saw", "bird feeder"];
    private static readonly string[] NewsTopics = ["composting", "rainwater harvesting", "bee-friendly planting", "winter pruning", "tomato blight", "companion planting", "raised beds"];
    private static readonly (string Name, string Addr)[] People =
        [("Ada Example", "ada@example.com"), ("Grace Sample", "grace@example.net"), ("Kenji Example", "kenji@example.jp"), ("Pat Doe", "pat@example.com"), ("Robin Test", "robin@example.net")];
    private static readonly string[] FamilyTopics = ["dinner on Sunday", "the school play", "borrowing the ladder", "birthday plans", "the broken fence", "the cat sitter", "the car share"];
    private static readonly string[] Codenames = ["Heron", "Kestrel", "Osprey", "Wren", "Plover"];
    private static readonly string[] Statuses = ["on track", "blocked on supplier", "waiting for review", "ahead of schedule", "slipping a week"];
    private static readonly string[] Destinations = ["Porto", "Edinburgh", "Kyoto", "Oslo", "Valencia", "Galway"];
    private static readonly int[] Offsets = [0, -5, 1, 9, -8, 2];

    public static IReadOnlyList<MailFile> Build(Catalog catalog, int count)
    {
        var rng = new Random(20260925);
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var files = new List<MailFile>(count);

        for (var i = 1; i <= count; i++)
        {
            var id = $"filler-{i:0000}@devcorpus.example";
            var date = new DateTimeOffset(start.AddDays(rng.Next(0, 900)).AddMinutes(rng.Next(0, 24 * 60)),
                TimeSpan.FromHours(Offsets[rng.Next(Offsets.Length)]));
            var (folder, from, subject, body) = (i % 5) switch
            {
                0 => Receipt(rng, i),
                1 => Newsletter(rng),
                2 => Family(rng),
                3 => Work(rng),
                _ => Travel(rng, i),
            };
            // One in ten still unread in new/, as a live mailbox has.
            var flags = folder == "INBOX" && rng.Next(10) == 0 ? null : "S";
            files.Add(catalog.Mail(folder,
                Catalog.Message(id, from, Catalog.Owner, subject, date, TextPlain(body)), flags));
        }
        return files;
    }

    private static (string, string, string, string) Receipt(Random rng, int n)
    {
        var shop = Shops[rng.Next(Shops.Length)];
        var item = Items[rng.Next(Items.Length)];
        var qty = rng.Next(1, 5);
        var total = qty * (rng.Next(150, 4000) / 100m);
        return ("Receipts", $"{shop} <receipts@{Slug(shop)}.example>", $"Your receipt from {shop} (#{10000 + n})",
            $"Thanks for shopping at {shop}.\n\nItem: {item} x{qty}\nTotal: {total:0.00} GBP\n\n"
            + "Keep this email as your receipt. Returns are accepted within 30 days with proof of purchase.\n");
    }

    private static (string, string, string, string) Newsletter(Random rng)
    {
        var topic = NewsTopics[rng.Next(NewsTopics.Length)];
        return ("Newsletters", "The Weekly Sprout <news@sprout.example>", $"The Weekly Sprout: notes on {topic}",
            $"This week we look at {topic}.\n\nReaders wrote in with questions about {topic} after last month's issue, "
            + $"so here are the three things that matter most, and one myth about {topic} that refuses to die.\n");
    }

    private static (string, string, string, string) Family(Random rng)
    {
        var (name, addr) = People[rng.Next(People.Length)];
        var topic = FamilyTopics[rng.Next(FamilyTopics.Length)];
        return ("INBOX", $"{name} <{addr}>", $"About {topic}",
            $"Hi Sam,\n\nQuick one about {topic} — does the weekend still work? If not, next week is\n"
            + $"fine too; I'd just like to have it sorted before the end of the month. Let me know.\n\n{name.Split(' ')[0]}\n");
    }

    private static (string, string, string, string) Work(Random rng)
    {
        var code = Codenames[rng.Next(Codenames.Length)];
        var status = Statuses[rng.Next(Statuses.Length)];
        return ("Archive", "Project Bot <bot@example.com>", $"Project {code} status: {status}",
            $"Project {code} is {status}.\n\nOpen items: {rng.Next(2, 30)}. Next review in {rng.Next(1, 4)} weeks.\n"
            + $"Owners should update their tickets before the review so the {code} board reflects reality.\n");
    }

    private static (string, string, string, string) Travel(Random rng, int n)
    {
        var dest = Destinations[rng.Next(Destinations.Length)];
        var reference = $"NW{n:0000}{(char)('A' + rng.Next(26))}";
        return ("INBOX", "Northwind Travel <bookings@northwind.example>", $"Booking confirmation {reference}: {dest}",
            $"Your trip to {dest} is confirmed.\n\nBooking reference: {reference}\nTravellers: {rng.Next(1, 5)}\n\n"
            + $"Check in online from 48 hours before departure. Your itinerary for {dest} is attached to your account.\n");
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
