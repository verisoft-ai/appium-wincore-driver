namespace Wincore.ServerSdk;

/// <summary>
/// Thrown when a locator expression is syntactically invalid (e.g. malformed
/// XPath). The host maps this to the wire error code <c>InvalidSelector</c>, which
/// the TS client turns into Appium's InvalidSelectorError.
/// </summary>
public sealed class InvalidSelectorException : Exception
{
    public InvalidSelectorException(string message) : base(message) { }
}
