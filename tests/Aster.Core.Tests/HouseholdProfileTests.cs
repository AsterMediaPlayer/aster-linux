using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Plex;
using Aster.Core.Profiles;

namespace Aster.Core.Tests;

/// <summary>A household's profiles, from Bonfire and a Plex Home. The Apple app's HouseholdProfileTests.</summary>
public class HouseholdProfileTests
{
    private const string Master = "8E3CDFA5-79A8-4BB9-BD9A-0E96B7DC974A";
    private const string Kids = "11111111-2222-3333-4444-555555555555";
    private const string Neighbour = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    /// <summary>The plugin's own pages get camelCase; Jellyfin's serializer gives PascalCase. The third entry is a linked household's, in PascalCase.</summary>
    private const string ListJson = $$"""
        [
          {"profileUserId":"{{Master}}","profileName":"Bard","avatarInitial":"B","avatarColor":"#00A4DC",
           "requiresPin":true,"hasPin":true,"isMaster":true,"isBonfire":false,"profileImage":null,"masterUserId":"{{Master}}"},
          {"profileUserId":"{{Kids}}","profileName":"Kids","avatarInitial":"K","avatarColor":"#FF6B35",
           "requiresPin":false,"hasPin":false,"isMaster":false,"profileImage":"fox","masterUserId":"{{Master}}"},
          {"ProfileUserId":"{{Neighbour}}","ProfileName":"Neighbour","RequiresPin":true,"HasPin":true,"IsMaster":true,
           "IsBonfire":true,"ProfileImage":"https://example.com/pic.jpg","MasterUserId":"{{Neighbour}}"}
        ]
        """;

    private readonly BonfireClient client = new(new JellyfinClient(new Uri("https://media.example"), "t", "u"));

    private static List<BonfireProfileRecord> Records() => JsonSerializer.Deserialize<List<BonfireProfileRecord>>(ListJson, Bonfire.Json)!;

    [Fact]
    public void TheListReadsEitherSpelling()
    {
        var records = Records();
        Assert.Equal(3, records.Count);
        Assert.Equal("Bard", records[0].ProfileName);
        Assert.True(records[0].RequiresPin);
        Assert.Equal("Neighbour", records[2].ProfileName);
        Assert.True(records[2].IsBonfire);
        Assert.Equal("https://example.com/pic.jpg", records[2].ProfileImage);
    }

    /// <summary>The sign-in keeps the id without dashes; the plugin sends it with.</summary>
    [Fact]
    public void TheCurrentProfileIsFoundEitherWayTheIdIsWritten()
    {
        var profiles = Records().Select(r => BonfireProfiles.FromBonfire(r, client, "8e3cdfa579a84bb9bd9a0e96b7dc974a")).ToList();
        Assert.Equal([true, false, false], profiles.Select(p => p.IsCurrent));
        Assert.Equal("8e3cdfa579a84bb9bd9a0e96b7dc974a", profiles[0].Id);
        Assert.Equal(Master, profiles[0].RawId);
        Assert.Equal("8e3cdfa579a84bb9bd9a0e96b7dc974a", profiles[1].MasterUserId);
        Assert.Equal("8e3cdfa579a84bb9bd9a0e96b7dc974a", profiles[1].StoredHousehold.MasterUserId);
        Assert.False(profiles[1].StoredHousehold.IsMaster);
        Assert.True(profiles[0].StoredHousehold.IsMaster);
    }

    [Fact]
    public void PicturesComeFromWhereverTheyAre()
    {
        var profiles = Records().Select(r => BonfireProfiles.FromBonfire(r, client, "x")).ToList();
        // None: the initial on the colour.
        Assert.Null(profiles[0].ImageUrl);
        Assert.Equal("B", profiles[0].Initial);
        Assert.Equal("#00A4DC", profiles[0].ColorHex);
        // Stored on the server: the plugin's image route, small.
        var kids = profiles[1].ImageUrl!;
        Assert.EndsWith($"/plugins/profiles/image/{Kids}", kids.AbsolutePath);
        Assert.Contains("size=thumb", kids.Query);
        // Elsewhere: straight from there.
        Assert.Equal(new Uri("https://example.com/pic.jpg"), profiles[2].ImageUrl);
        Assert.True(profiles[2].IsOtherHousehold);
        // No initial sent: the name's first letter.
        Assert.Equal("N", profiles[2].Initial);
        Assert.Equal(Bonfire.DefaultAvatarColor, profiles[2].ColorHex);
    }

    [Fact]
    public void ASwitchResultReadsEitherSpelling()
    {
        var camel = JsonSerializer.Deserialize<BonfireSwitchResult>($$"""{"activeProfileToken":"abc","jellyfinUserId":"{{Kids}}","libraryArtwork":[]}""", Bonfire.Json)!;
        Assert.Equal("abc", camel.ActiveProfileToken);
        var pascal = JsonSerializer.Deserialize<BonfireSwitchResult>($$"""{"ActiveProfileToken":"abc","JellyfinUserId":"{{Kids}}"}""", Bonfire.Json)!;
        Assert.Equal(Kids, pascal.JellyfinUserId);
    }

    /// <summary>A saved sign-in is reused only where no PIN is asked: a profile with one goes through the server every time.</summary>
    [Fact]
    public void TheWayToAProfileNeverSkipsItsPin()
    {
        var profiles = Records().Select(r => BonfireProfiles.FromBonfire(r, client, "8e3cdfa579a84bb9bd9a0e96b7dc974a")).ToList();
        var saved = new StoredSession
        {
            ServerUrl = new Uri("https://media.example"), ServerName = "Media", UserId = profiles[1].Id,
            UserName = "Kids", AccessToken = "kids-token", ServerKind = ServerKind.Jellyfin,
        };
        Assert.IsType<ProfileSwitchPlan.AlreadyCurrent>(ProfileSwitchPlan.Make(profiles[0], null));
        Assert.Equal(new ProfileSwitchPlan.ReuseStored(saved), ProfileSwitchPlan.Make(profiles[1], saved));
        Assert.IsType<ProfileSwitchPlan.SwitchNow>(ProfileSwitchPlan.Make(profiles[1], null));
        Assert.IsType<ProfileSwitchPlan.NeedsPin>(ProfileSwitchPlan.Make(profiles[2], saved));
        Assert.IsType<ProfileSwitchPlan.NeedsPin>(ProfileSwitchPlan.Make(profiles[0] with { IsCurrent = false }, saved));
    }

    [Fact]
    public void APlexHomeUserBecomesAProfile()
    {
        var link = new PlexLink { AccountToken = "a", MachineId = "m1", HomeUserUuid = "u2", HomeUserHasPin = false, HomeToken = "home" };
        var locked = PlexHomeProfiles.FromPlex(new PlexHomeUser { Id = 1, Uuid = "u1", Title = "Peppy", Thumb = "https://plex.tv/users/1/avatar", Admin = true, Protected = true },
            link, "u2");
        Assert.True(locked.RequiresPin && locked.HasPin && locked.IsMaster && !locked.IsCurrent);
        Assert.Equal(new Uri("https://plex.tv/users/1/avatar"), locked.ImageUrl);
        var open = PlexHomeProfiles.FromPlex(new PlexHomeUser { Id = 2, Uuid = "u2", Title = "kid" }, link, "u2");
        Assert.True(!open.RequiresPin && open.IsCurrent && open.ImageUrl is null && open.Initial == "K");
    }
}

/// <summary>Adding someone to the household. The Apple app's ProfileCreationTests.</summary>
public class ProfileCreationTests
{
    private readonly BonfireClient client = new(new JellyfinClient(new Uri("https://media.example"), "t", "u"));

    [Fact]
    public void TheFormChecksWhatTheServerWould()
    {
        var draft = new ProfileDraft();
        Assert.Equal("Enter a name for the profile.", draft.ValidationMessage(""));
        draft = draft with { Name = new string('a', 33) };
        Assert.Contains("at most 32", draft.ValidationMessage(""));
        draft = draft with { Name = "Kids <3" };
        Assert.Contains("letters, numbers", draft.ValidationMessage(""));
        draft = draft with { Name = "  Kids' Room  " };
        Assert.Null(draft.ValidationMessage(""));
        Assert.Equal("Kids' Room", draft.TrimmedName);
        Assert.Equal("K", draft.Initial);

        Assert.Equal("A PIN is 4 to 8 digits.", (draft with { Pin = "123" }).ValidationMessage("123"));
        Assert.Equal("A PIN is 4 to 8 digits.", (draft with { Pin = "12a4" }).ValidationMessage("12a4"));
        Assert.Equal("A PIN is 4 to 8 digits.", (draft with { Pin = "123456789" }).ValidationMessage("123456789"));
        Assert.Equal("The PINs don't match.", (draft with { Pin = "2468" }).ValidationMessage("2469"));
        Assert.Null((draft with { Pin = "2468" }).ValidationMessage("2468"));
    }

    [Fact]
    public void TheServersListsBecomeChoices()
    {
        var avatars = JsonSerializer.Deserialize<BonfireAvatarLibrary>("""
            {"AllowCustomUploads":true,"StarterAvatarsEnabled":true,"Avatars":[
              {"Id":"lib-1","DisplayName":"Our Dog","Url":"/plugins/profiles/avatars/lib-1","ThumbUrl":"/plugins/profiles/avatars/lib-1?size=thumb","IsStarter":false},
              {"Id":"starter-fox","DisplayName":"Fox","Url":"/plugins/profiles/avatars/starter-fox","IsStarter":true}]}
            """, Bonfire.Json);
        var libraries = JsonSerializer.Deserialize<List<BonfireLibrary>>("""[{"Id":"f1","Name":"Films","CollectionType":"movies"},{"id":"k1","name":"Kids","collectionType":"tvshows"}]""", Bonfire.Json);
        var ratings = JsonSerializer.Deserialize<BonfireRatings>("""{"Country":"GB","Ratings":[{"Score":12,"Labels":["12","12A"]},{"Score":0,"Labels":["U"]}]}""", Bonfire.Json);

        var options = BonfireProfiles.Options(avatars, libraries, ratings, client);
        Assert.Equal(["Our Dog", "Fox"], options.Avatars.Select(a => a.Name));
        Assert.EndsWith("/plugins/profiles/avatars/starter-fox", options.Avatars[1].Url.AbsolutePath);
        Assert.Equal(["f1", "k1"], options.Libraries.Select(l => l.Id));
        // Youngest first, labels joined.
        Assert.Equal(["U", "12 / 12A"], options.Ratings.Select(r => r.Label));
        Assert.Equal([0, 12], options.Ratings.Select(r => r.Score));
        Assert.Equal("GB", options.RatingCountry);

        // A list the server couldn't give is just empty.
        var bare = BonfireProfiles.Options(null, null, null, client);
        Assert.True(bare.Avatars.Count == 0 && bare.Libraries.Count == 0 && bare.Ratings.Count == 0);
    }

    [Fact]
    public void TheDraftGoesAsThePluginTakesIt()
    {
        var draft = new ProfileDraft
        {
            Name = " Kids ", Pin = "2468", BypassPinOnLocalNetwork = true, AvatarId = "starter-fox", ColorHex = "#FF6B35",
            RatingScore = 12, LibraryIds = new HashSet<string> { "k1", "f1" }, MasterPin = "1234",
        };
        var body = JsonDocument.Parse(JsonSerializer.Serialize(BonfireCreateRequest.From(draft), Bonfire.Sending)).RootElement;
        Assert.Equal("Kids", body.GetProperty("profileName").GetString());
        Assert.Equal("2468", body.GetProperty("pin").GetString());
        Assert.True(body.GetProperty("bypassPinOnLocalNetwork").GetBoolean());
        Assert.Equal("starter-fox", body.GetProperty("avatarLibraryId").GetString());
        Assert.Equal("#FF6B35", body.GetProperty("avatarColor").GetString());
        Assert.Equal("12", body.GetProperty("maxParentalRating").GetString());
        Assert.Equal(["f1", "k1"], body.GetProperty("enabledFolders").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("1234", body.GetProperty("masterPin").GetString());

        // No PIN: none sent, and nothing about skipping it; every library: the key left out, which the plugin reads as all, now and later.
        var open = JsonDocument.Parse(JsonSerializer.Serialize(BonfireCreateRequest.From(new ProfileDraft { Name = "Guest" }), Bonfire.Sending)).RootElement;
        foreach (var key in new[] { "pin", "bypassPinOnLocalNetwork", "enabledFolders", "maxParentalRating", "masterPin", "avatarLibraryId" })
            Assert.False(open.TryGetProperty(key, out _), key);
        Assert.Equal(Bonfire.DefaultAvatarColor, open.GetProperty("avatarColor").GetString());
    }

    [Fact]
    public void ACreatedProfileReadsEitherSpelling()
    {
        var made = JsonSerializer.Deserialize<BonfireCreatedProfile>("""{"ProfileUserId":"11111111-2222-3333-4444-555555555555","ProfileName":"Kids"}""", Bonfire.Json)!;
        Assert.Equal("11111111222233334444555555555555", HouseholdProfile.PlainId(made.ProfileUserId));
        Assert.Equal("Kids", made.ProfileName);
    }
}

/// <summary>Whether Aster asks who's watching as it opens. The Apple app's ProfileGateTests.</summary>
public class ProfileGateTests
{
    private static readonly Uri Server = new("https://media.example");

    private static readonly StoredHousehold Household = new()
    {
        MasterUserId = "8e3cdfa579a84bb9bd9a0e96b7dc974a", IsMaster = false, AvatarInitial = "K", AvatarColor = "#FF6B35", ProfileImage = "fox",
    };

    private static StoredSession Plain => new() { ServerUrl = Server, ServerName = "Media", UserId = "u1", UserName = "bard", AccessToken = "t", ServerKind = ServerKind.Jellyfin };

    private static StoredSession InHousehold => Plain with { Household = Household };

    private static StoredSession PlexHome => new()
    {
        ServerUrl = Server, ServerName = "Plex", UserId = "u2", UserName = "kid", AccessToken = "t", ServerKind = ServerKind.Plex,
        Plex = new PlexLink { AccountToken = "a", MachineId = "m", HomeUserUuid = "u2", HomeUserHasPin = false, HomeToken = "home" },
    };

    [Fact]
    public void AnOrdinarySignInIsNeverAsked()
    {
        foreach (var mode in Enum.GetValues<ProfileGateMode>())
            foreach (var cached in new bool?[] { null, true, false })
                Assert.False(ProfileGate.ShouldAsk(Plain, mode, cached));
    }

    [Fact]
    public void AHouseholdFollowsTheSettingHere()
    {
        Assert.True(ProfileGate.ShouldAsk(InHousehold, ProfileGateMode.Always, null));
        Assert.False(ProfileGate.ShouldAsk(InHousehold, ProfileGateMode.Never, true));
        // As the server is set: only once its preference has been read, and says so.
        Assert.True(ProfileGate.ShouldAsk(InHousehold, ProfileGateMode.Server, true));
        Assert.False(ProfileGate.ShouldAsk(InHousehold, ProfileGateMode.Server, false));
        Assert.False(ProfileGate.ShouldAsk(InHousehold, ProfileGateMode.Server, null));
    }

    /// <summary>A Plex Home has no setting of its own: only Every Time asks.</summary>
    [Fact]
    public void APlexHomeAsksOnlyEveryTime()
    {
        Assert.True(ProfileGate.ShouldAsk(PlexHome, ProfileGateMode.Always, null));
        Assert.False(ProfileGate.ShouldAsk(PlexHome, ProfileGateMode.Server, true));
        Assert.False(ProfileGate.ShouldAsk(PlexHome, ProfileGateMode.Never, null));
        // Signed in before the Home's token was kept: nothing to switch with.
        var older = PlexHome with { Plex = PlexHome.Plex! with { HomeToken = null } };
        Assert.False(ProfileGate.ShouldAsk(older, ProfileGateMode.Always, null));
    }

    [Fact]
    public void TheHouseholdRoundTripsAndOlderSignInsStillRead()
    {
        var decoded = JsonSerializer.Deserialize<StoredSession>(JsonSerializer.Serialize(InHousehold, JellyfinJson.Options), JellyfinJson.Options)!;
        Assert.Equal(InHousehold.Household, decoded.Household);
        Assert.Equal("https://media.example/|8e3cdfa579a84bb9bd9a0e96b7dc974a", decoded.HouseholdKey);

        // Saved before households were kept.
        var older = JsonSerializer.Deserialize<StoredSession>("""{"serverUrl":"https://media.example","userId":"u1","userName":"bard","accessToken":"t"}""", JellyfinJson.Options)!;
        Assert.Null(older.Household);
        Assert.Null(older.HouseholdKey);
    }

    /// <summary>A profile's password on the server is its PIN; the main account keeps its password.</summary>
    [Fact]
    public void AProfileAsksForItsPin()
    {
        Assert.Equal("PIN", new SavedServer { Session = InHousehold }.CredentialName);
        Assert.True(new SavedServer { Session = InHousehold }.IsHouseholdProfile);
        var master = InHousehold with { Household = Household with { IsMaster = true } };
        Assert.Equal("password", new SavedServer { Session = master }.CredentialName);
        Assert.Equal("password", new SavedServer { Session = Plain }.CredentialName);
    }
}
