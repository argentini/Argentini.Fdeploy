namespace Argentini.Fdeploy.Domain;

public sealed class OfflineSettings
{
    public string MetaTitle { get; set; } = "Unavailable for Maintenance";
    public string PageTitle { get; set; } = "Unavailable for Maintenance";
    public string PageHtml { get; set; } = "<p>The website is being updated and should be available shortly.</p><p><strong>Check back soon!</strong></p>";
}