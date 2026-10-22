using System;
using Meta.Utilities.MetaModelAPI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class OpenRouterRequestTests
{
    [TestCase("openai/gpt-4o")]
    [TestCase("openrouter/free")]
    [TestCase("google/model:free?paid=1")]
    public void PaidAndImplicitRoutersAreRejected(string model) =>
        Assert.Throws<ArgumentException>(() => OpenRouterRequest.ValidateModel(model));

    [Test]
    public void CustomEndpointIsRejected() =>
        Assert.Throws<ArgumentException>(() => OpenRouterRequest.ValidateEndpoint("http://localhost:8080/"));

    [Test]
    public void ImageRequestHasCleanPartsAndZeroPriceCeilings()
    {
        var json = JObject.Parse(OpenRouterRequest.Image(OpenRouterRequest.DefaultModel,
            "用繁體中文\nReturn JSON.", "A \"cup\" \\ 茶杯", new[] { "data:image/png;base64,YQ==" }));
        Assert.That((string)json["messages"][0]["content"], Is.EqualTo("用繁體中文\nReturn JSON."));
        var parts = (JArray)json["messages"][1]["content"];
        Assert.That((string)parts[0]["text"], Is.EqualTo("A \"cup\" \\ 茶杯"));
        Assert.That(parts[0]["image_url"], Is.Null);
        Assert.That(parts[1]["text"], Is.Null);
        Assert.That((bool)json["provider"]["allow_fallbacks"], Is.False);
        Assert.That((bool)json["reasoning"]["enabled"], Is.False);
        foreach (var price in ((JObject)json["provider"]["max_price"]).Properties())
            Assert.That((decimal)price.Value, Is.Zero, price.Name);
        Assert.That(json["reasoning_effort"], Is.Null);
    }

    [Test]
    public void TextChatRemainsPlainTextAndPreservesHistory()
    {
        var api = new MetaModelRestApi();
        var history = api.StartNewChat("Translate this");
        api.AddChatHistoryAsUser(history, "你好");
        var json = JObject.Parse(OpenRouterRequest.Text(OpenRouterRequest.DefaultModel, history));
        Assert.That((string)json["messages"][1]["content"], Is.EqualTo("你好"));
        Assert.That(json["response_format"], Is.Null);
    }
}
