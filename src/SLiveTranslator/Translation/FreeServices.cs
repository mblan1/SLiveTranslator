namespace SLiveTranslator.Translation;

/// <summary>
/// The free translation services, kept for the whole run of the app: Google remembers that this network is blocked
/// and MyMemory that its daily quota is used up, and saving the settings must not forget either and try again.
/// </summary>
public sealed class FreeServices
{
    MyMemoryTranslator? _myMemory;
    string _myMemoryEmail = "";

    public GoogleTranslator Google { get; } = new();

    /// <summary>MyMemory for this email; a new one only when the email changed (the quota belongs to the email).</summary>
    public MyMemoryTranslator MyMemory(string email)
    {
        email = email.Trim();
        if (_myMemory is null || email != _myMemoryEmail)
        {
            _myMemory = new MyMemoryTranslator(email);
            _myMemoryEmail = email;
        }
        return _myMemory;
    }

    /// <summary>Google, then MyMemory when Google is blocked or fails.</summary>
    /// <remarks>"free-mt": cache entries made before MyMemory preferred machine translation held junk corpus matches.</remarks>
    public ITranslator Chain(string myMemoryEmail) => new FallbackTranslator("free-mt", Google, MyMemory(myMemoryEmail));
}
