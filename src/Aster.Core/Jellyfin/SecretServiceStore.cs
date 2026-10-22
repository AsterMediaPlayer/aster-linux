// Linux's counterpart of Windows Credential Manager: the signed-in session is
// kept by the desktop's Secret Service (KWallet on KDE, GNOME Keyring on
// GNOME), through libsecret, under "Aster" with an account attribute. It's
// reached through libsecret's non-variadic calls (the …v_sync ones), which
// can be bound without C's varargs.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Aster.Core.Jellyfin;

[SupportedOSPlatform("linux")]
public static unsafe class SecretServiceStore
{
    private const string SchemaName = "org.astermediaplayer.Aster";

    private static readonly Lazy<Api?> Loaded = new(Api.TryLoad);

    /// <summary>Whether the desktop has libsecret to talk to (it may still turn a call down).</summary>
    public static bool IsAvailable => Loaded.Value is not null;

    public static void Save(string data, string account)
    {
        var api = Loaded.Value ?? throw new InvalidOperationException("libsecret isn't installed");
        using var call = new Call(api, account);
        IntPtr error = 0;
        using var label = new Utf8($"Aster ({account})");
        using var collection = new Utf8("default");
        using var secret = new Utf8(data);
        var stored = api.Store(call.Schema, call.Attributes, collection, label, secret, 0, &error);
        Check(api, error);
        if (stored == 0) throw new InvalidOperationException("The Secret Service didn't keep the session");
    }

    public static string? Load(string account)
    {
        var api = Loaded.Value ?? throw new InvalidOperationException("libsecret isn't installed");
        using var call = new Call(api, account);
        IntPtr error = 0;
        var found = api.Lookup(call.Schema, call.Attributes, 0, &error);
        Check(api, error);
        if (found == 0) return null;
        try { return Marshal.PtrToStringUTF8(found); }
        finally { api.FreePassword(found); }
    }

    public static void Delete(string account)
    {
        var api = Loaded.Value ?? throw new InvalidOperationException("libsecret isn't installed");
        using var call = new Call(api, account);
        IntPtr error = 0;
        api.Clear(call.Schema, call.Attributes, 0, &error);
        Check(api, error);
    }

    /// <summary>A GError turned into an exception, and freed.</summary>
    private static void Check(Api api, IntPtr error)
    {
        if (error == 0) return;
        // GError: GQuark domain, gint code, gchar *message.
        var message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "Secret Service error";
        api.FreeError(error);
        throw new InvalidOperationException(message);
    }

    /// <summary>A NUL-terminated UTF-8 copy of a string, freed when done.</summary>
    private readonly struct Utf8 : IDisposable
    {
        public readonly IntPtr Pointer;
        public Utf8(string text) => Pointer = Marshal.StringToCoTaskMemUTF8(text);
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
        public static implicit operator IntPtr(Utf8 text) => text.Pointer;
    }

    /// <summary>
    /// The schema (Aster's name, one string attribute "account") and the
    /// attributes table naming this account, built for one call.
    /// </summary>
    private sealed class Call : IDisposable
    {
        // SecretSchema on 64-bit: name, flags (padded), 32 attributes of
        // {name, type (padded)}, reserved (padded), 7 reserved pointers.
        private const int SchemaSize = 8 + 8 + 32 * 16 + 8 + 7 * 8;

        private readonly Api api;
        private readonly Utf8 name = new(SchemaName);
        private readonly Utf8 attribute = new("account");
        private readonly Utf8 value;

        public readonly IntPtr Schema;
        public readonly IntPtr Attributes;

        public Call(Api api, string account)
        {
            this.api = api;
            value = new Utf8(account);
            Schema = Marshal.AllocHGlobal(SchemaSize);
            new Span<byte>((void*)Schema, SchemaSize).Clear();
            Marshal.WriteIntPtr(Schema, 0, name);
            // flags = SECRET_SCHEMA_NONE; attribute 0 = "account", SECRET_SCHEMA_ATTRIBUTE_STRING (0).
            Marshal.WriteIntPtr(Schema, 16, attribute);
            Attributes = api.NewTable(api.StrHash, api.StrEqual);
            api.Insert(Attributes, attribute, value);
        }

        public void Dispose()
        {
            api.Unref(Attributes);
            Marshal.FreeHGlobal(Schema);
            name.Dispose();
            attribute.Dispose();
            value.Dispose();
        }
    }

    /// <summary>The handful of libsecret and GLib calls this needs, found at run time.</summary>
    private sealed class Api
    {
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int> Store;
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr> Lookup;
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int> Clear;
        public delegate* unmanaged<IntPtr, void> FreePassword;
        public delegate* unmanaged<IntPtr, void> FreeError;
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr> NewTable;
        public delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> Insert;
        public delegate* unmanaged<IntPtr, void> Unref;
        public IntPtr StrHash;
        public IntPtr StrEqual;

        public static Api? TryLoad()
        {
            if (!NativeLibrary.TryLoad("libsecret-1.so.0", out var secret) || !NativeLibrary.TryLoad("libglib-2.0.so.0", out var glib)) return null;
            try
            {
                return new Api
                {
                    Store = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int>)NativeLibrary.GetExport(secret, "secret_password_storev_sync"),
                    Lookup = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)NativeLibrary.GetExport(secret, "secret_password_lookupv_sync"),
                    Clear = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)NativeLibrary.GetExport(secret, "secret_password_clearv_sync"),
                    FreePassword = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(secret, "secret_password_free"),
                    FreeError = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(glib, "g_error_free"),
                    NewTable = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(glib, "g_hash_table_new"),
                    Insert = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport(glib, "g_hash_table_insert"),
                    Unref = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(glib, "g_hash_table_unref"),
                    StrHash = NativeLibrary.GetExport(glib, "g_str_hash"),
                    StrEqual = NativeLibrary.GetExport(glib, "g_str_equal"),
                };
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }
    }
}
