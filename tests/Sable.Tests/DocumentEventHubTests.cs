using Sable.Plugins;

namespace Sable.Tests;

/// <summary>Per-plugin document-event fan-out (capability document.events).</summary>
public sealed class DocumentEventHubTests
{
    [Fact]
    public void Raise_invokes_subscribed_handlers()
    {
        var hub = new DocumentEventHub();
        int doc = 0, sel = 0, active = 0;
        hub.OnDocumentChanged("p", () => doc++);
        hub.OnSelectionChanged("p", () => sel++);
        hub.OnActiveDocumentChanged("p", () => active++);

        Assert.True(hub.HasSubscribers);
        hub.RaiseDocumentChanged();
        hub.RaiseSelectionChanged();
        hub.RaiseActiveDocumentChanged();
        hub.RaiseDocumentChanged();

        Assert.Equal(2, doc);
        Assert.Equal(1, sel);
        Assert.Equal(1, active);
    }

    [Fact]
    public void RemoveOwner_drops_a_plugins_handlers_only()
    {
        var hub = new DocumentEventHub();
        int a = 0, b = 0;
        hub.OnDocumentChanged("a", () => a++);
        hub.OnDocumentChanged("b", () => b++);

        hub.RemoveOwner("a");
        hub.RaiseDocumentChanged();

        Assert.Equal(0, a);   // dropped
        Assert.Equal(1, b);   // still fires
    }

    [Fact]
    public void No_subscribers_is_reported()
    {
        var hub = new DocumentEventHub();
        Assert.False(hub.HasSubscribers);
        hub.OnDocumentChanged("p", () => { });
        Assert.True(hub.HasSubscribers);
        hub.Clear();
        Assert.False(hub.HasSubscribers);
    }
}
