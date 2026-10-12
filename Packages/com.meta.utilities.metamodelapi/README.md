# Meta Model API

Meta Model API is Meta's developer API for the Muse family of models.

## Overview

While the model can be used to accomplish many goals, the ones outlined here are targeted toward common use cases:

- Ask questions, e.g.: 'What is a synonym for chair?'
- Make queries about images, e.g.: 'What is in this image: {image}'
- Translate data, e.g.: 'Translate "Hello" into Vietnamese.'
- Format data or answers into JSON

Getting the desired output from an AI relies mostly on constructing "prompts" (questions with context) worded in particular ways to generate suitable responses.

Multiple models are available with different context windows and capabilities. The Muse Spark family (`muse-spark-1.1` through `muse-spark-1.3`) handles text and image understanding through a single chat endpoint.

For more information: [`Meta Model API Docs`](https://ai.developer.meta.com/docs/getting-started/overview)

### Getting Started

Create an Account at: [`Meta Model API`](https://ai.developer.meta.com/).

Create an API Key to pass in requests, from the **API keys** tab of the dashboard. You only see the key once, so save it immediately. Keys start with `LLM|`. Treat the key as a secret: keep it out of source control and out of builds you ship (see [Configuration](#configuration)).

## Installation

You can integrate this package into your own project by using the Package Manager to [add the following Git URL](https://docs.unity3d.com/Manual/upm-ui-giturl.html):

```txt
https://github.com/oculus-samples/Unity-SpatialLingo.git?path=Packages/com.meta.utilities.metamodelapi
```

## API Overview

Requests should start with a base URL: `https://api.meta.ai/v1`, and end with an endpoint path. Here only the `/chat/completions` endpoint is described. The protocol is OpenAI-compatible.

### Choosing a Model

Which models a given key can reach varies by key and account tier, so a hardcoded model id may fail with `model_not_found`. List what a key can actually use:

```bash
export MODEL_API_KEY='YOUR_UNIQUE_META_MODEL_API_KEY_GOES_HERE'

curl -X GET https://api.meta.ai/v1/models \
-H "Authorization: Bearer $MODEL_API_KEY"
```

```json
{
    "object": "list",
    "data": [
        { "id": "muse-spark-1.3", "object": "model", "created": 0, "owned_by": "meta" }
    ]
}
```

Pick an id from that list and set it on the client (see [Selecting a Model](#selecting-a-model)). The examples in this document use `muse-spark-1.3`; replace it with an id your key can reach.

### Reasoning and Token Budgets

Muse Spark always reasons before it answers, and **reasoning tokens are charged against the completion budget**. If `max_completion_tokens` is too small, the model spends the whole budget thinking and returns a `null` content with `"finish_reason": "length"`:

```json
{
    "choices": [
        {
            "finish_reason": "length",
            "index": 0,
            "message": { "content": null, "role": "assistant" }
        }
    ],
    "usage": { "completion_tokens": 32, "completion_tokens_details": { "reasoning_tokens": 29 } }
}
```

Budget for the reasoning as well as the answer, and use `reasoning_effort` (`minimal`, `low`, `medium`, `high`) to trade quality against latency and cost. `MetaModelRestApi` sends `minimal` by default, the lowest effort the service accepts; `none` is rejected.

### Text Chat Completion

A chat consists of: an (optional) system prompt, and a list of (at least one) user messages. The system role is used to define how the AI should behave. The user role is used to pass messages or similar information to the AI on behalf of the user. An example curl command:

```bash
curl -X POST https://api.meta.ai/v1/chat/completions \
-H "Content-Type: application/json" \
-H "Authorization: Bearer $MODEL_API_KEY" \
-d '{
    "model": "muse-spark-1.3",
    "messages": [
        {
            "role": "system",
            "content": "You are a translation assistant. Translate English to Spanish. No quotes."
        },
        {
            "role": "user",
            "content": "Translate: Where is the nearest coffee shop?"
        }
    ],
    "max_completion_tokens": 1024,
    "reasoning_effort": "minimal"
}'
```

Example response:

```json
{
    "id": "chatcmpl-01a0874f-f364-7a42-b703-f8bc5c6d1396",
    "choices": [
        {
            "finish_reason": "stop",
            "index": 0,
            "message": {
                "content": "¿Dónde está la cafetería más cercana?",
                "refusal": null,
                "role": "assistant"
            },
            "logprobs": null
        }
    ],
    "created": 1788976558,
    "model": "muse-spark-1.3",
    "object": "chat.completion",
    "usage": {
        "completion_tokens": 119,
        "prompt_tokens": 34,
        "total_tokens": 153,
        "completion_tokens_details": { "reasoning_tokens": 100 }
    }
}
```

The completion text lives at `choices[0].message.content`.

### Image Understanding

An image can be passed, using a URL or a base64 encoded string. Note that `content` becomes an array of content parts rather than a plain string, and that **images are only processed on `user`-role messages**:

```bash
curl -X POST https://api.meta.ai/v1/chat/completions \
-H "Content-Type: application/json" \
-H "Authorization: Bearer $MODEL_API_KEY" \
-d '{
    "model": "muse-spark-1.3",
    "messages": [
    {
      "role": "system",
      "content": [
        {
          "type": "text",
          "text": "You are a helpful assistant that provides concise answers."
        }
      ]
    },
    {
      "role": "user",
      "content": [
        {
          "type": "text",
          "text": "What word best describes the object in this image? Only respond with the minimal amount of words. Do not include any punctuation."
        },
        {
          "type": "image_url",
          "image_url": {
            "url": "https://www.wikipedia.org/portal/wikipedia.org/assets/img/Wikipedia-logo-v2@2x.png"
          }
        }
      ]
    }
  ]
}'
```

Example response:

```json
{
    "id": "chatcmpl-01a131e4-997f-7407-bc88-92cdac5cb5ae",
    "choices": [
        {
            "finish_reason": "stop",
            "index": 0,
            "message": {
                "content": "globe",
                "refusal": null,
                "role": "assistant"
            },
            "logprobs": null
        }
    ],
    "created": 1791838399,
    "model": "muse-spark-1.3",
    "object": "chat.completion",
    "usage": {
        "completion_tokens": 517,
        "prompt_tokens": 230,
        "total_tokens": 747,
        "completion_tokens_details": { "reasoning_tokens": 505 }
    }
}
```

No `reasoning_effort` is set in this request, so the service default applies; most of the completion tokens are reasoning. Setting `"reasoning_effort": "minimal"` reduces them substantially.

Swap the `url` value for `data:image/jpeg;base64,...` to send a local image, and append more `image_url` parts for multi-image prompts.

Accepted MIME types are `image/jpeg`, `image/jpg`, `image/png`, `image/gif`, `image/webp`, and `image/x-icon`. Inline images are capped at 50 MB, with a maximum of 50 images per request. Token usage scales with resolution, so downscaling before sending meaningfully reduces cost.

### JSON Formatting

The AI can be prompted to return JSON objects rather than freeform statements:

```bash
curl -X POST https://api.meta.ai/v1/chat/completions \
-H "Content-Type: application/json" \
-H "Authorization: Bearer $MODEL_API_KEY" \
-d '{
    "model": "muse-spark-1.3",
    "messages": [
        {
            "role": "user",
            "content": "Dissect the following sentence into nouns, verbs, and adjectives. Return a json formatted object with keys: \"nouns\", \"verbs\", and \"adjectives\". Do not include any extra information, only the json object: \"Where is the beef?\""
        }
    ],
    "max_completion_tokens": 1024
}'
```

Example response:

```json
{
    "id": "chatcmpl-01a131e3-e15e-7492-ba5f-be0ed3283712",
    "choices": [
        {
            "finish_reason": "stop",
            "index": 0,
            "message": {
                "content": "{\"nouns\": [\"beef\"], \"verbs\": [\"is\"], \"adjectives\": []}",
                "refusal": null,
                "role": "assistant"
            },
            "logprobs": null
        }
    ],
    "created": 1791838338,
    "model": "muse-spark-1.3",
    "object": "chat.completion",
    "usage": {
        "completion_tokens": 784,
        "prompt_tokens": 58,
        "total_tokens": 842,
        "completion_tokens_details": { "reasoning_tokens": 754 }
    }
}
```

The JSON arrives as a string in `choices[0].message.content` and still needs to be parsed. For a hard guarantee rather than a prompt-level request, pass `"response_format": {"type": "json_object"}`.

## Configuration

Because querying the service requires a unique API key, the value needs to be configurable. The key should be stored securely and passed when initializing the API client.

### Security Considerations for Quest Apps

**Important:** Quest apps that are shipped to end users should **not** directly embed Meta Model API keys in the application. Doing so exposes your API key to anyone who inspects the app binary, potentially leading to unauthorized usage and unexpected charges.

**Recommended Approach:** Instead of embedding API keys directly, Quest apps should:

1. Authenticate the client/app with your own backend server
2. Have your server validate the client's identity and entitlements
3. Make Meta Model API calls from your server (or proxy to another service provider, or use your own hosted model with something like Ollama)

This approach keeps your API keys secure on the server side and gives you control over rate limiting, user authentication, and cost management.

### Custom API Key Provider

You can configure custom authentication logic using `MetaModelRestApi.GetApiKeyAsync`. This static delegate allows you to provide dynamic API keys and base URLs at runtime:

```csharp
using Meta.Utilities.MetaModelAPI;

[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
public static void ConfigureCustomAuth()
{
    MetaModelRestApi.GetApiKeyAsync = async () =>
    {
        // Example: Authenticate with your backend server
        string userToken = await AuthenticateWithYourServer();
        string serverProxyUrl = "https://your-server.com/model-proxy/v1/";

        // Return tuple: (apiKey/token, baseUrl)
        // baseUrl can be null to use the default Meta Model API endpoint
        return (userToken, serverProxyUrl);
    };
}
```

The function should return a tuple containing:

- `string apiKey`: The API key or authentication token to use
- `string baseUrl`: The base URL for the API endpoint (or `null` to use the default `https://api.meta.ai/v1/`)

The base URL **must end with `/`**, as in the example above. Endpoint paths such as `chat/completions` are resolved relative to it, so without the trailing slash its last segment is dropped (`https://your-server.com/model-proxy/v1` would send requests to `https://your-server.com/model-proxy/chat/completions`).

### Selecting a Model

`MetaModelRestApi.DefaultModel` sets the model for every client created from that point on, which is the usual way to configure it once at startup:

```csharp
MetaModelRestApi.DefaultModel = "muse-spark-1.3";
```

Individual clients can override it after construction via the `Model` property, and `Effort` controls how much the model reasons:

```csharp
var api = new MetaModelRestApi
{
    Model = "muse-spark-1.2",
    Effort = MetaModelRestApi.ReasoningEffort.Medium,
};
```

### Development Configuration

For development and testing purposes only, you can configure your API key and model by modifying [Assets/SpatialLingo/Resources/ScriptableSettings/SpatialLingoSettings.asset](../../Assets/SpatialLingo/Resources/ScriptableSettings/SpatialLingoSettings.asset) — set `MetaModelApiKey`, and `MetaModelId` if the default model is not available to your key. This approach is suitable for local development but should not be used in production builds shipped to users.

## C# REST API

[MetaModelRestApi.cs](../com.meta.utilities.metamodelapi/Runtime/Scripts/MetaModelRestApi.cs) encapsulates the endpoint requests, converting elements to/from C# objects to URL requests.

You can start a new chat sequence using `api.StartNewChat(SystemPrompt);` and continue the chat with additional messages. Giving the API more history allows for more conversational based interactions:

```csharp
var api = new MetaModelRestApi();
var chat = api.StartNewChat("You are a language assistant.");
var result = await api.ContinueChat(chat, "Translate \"Cat\" from English into Vietnamese.");
// eg result: The translation of "Cat" from English into Vietnamese is "Mèo".
```

`ContinueChat` and `ImageUnderstanding` return `null` when the service returns no usable completion — including the exhausted-reasoning-budget case described above, which is logged with its `finish_reason`. Callers should null-check the result.

Client defaults:

- **Model:** `MetaModelRestApi.DefaultModel` (`muse-spark-1.3`) at construction; see [Selecting a Model](#selecting-a-model).
- **Reasoning effort:** `ReasoningEffort.Minimal`.
- **Completion budget:** `max_completion_tokens` of 4096, generous because reasoning tokens count against it.
- **Request timeout:** 60 seconds. A request that gets no reply in time is reported as a timeout (see [Service Health](#service-health)).

### Request Metrics

Every response carries a `Usage` object (`MetaModelRestApi.ModelUsage`) with the metrics the service reported for that request, plus the measured round trip:

- `PromptTokens`, `CompletionTokens` (reasoning tokens included) and `TotalTokens`
- `Model` — the model that actually served the request, which need not be the one requested
- `FinishReason` — for example `stop`, or `length` when the completion budget ran out
- `RoundTripMilliseconds` — wall-clock time from sending the request to the parsed reply (local work such as encoding an image is not included)
- `CompletionTokensPerSecond` — completion tokens over the round trip

```csharp
var response = await api.ContinueChat(chat, "Translate \"Cat\" into Vietnamese.");
if (response != null)
{
    var usage = response.Usage;
    Debug.Log($"{usage.Model}: {usage.TotalTokens} tokens ({usage.CompletionTokens} completion) in {usage.RoundTripMilliseconds} ms, finish: {usage.FinishReason}");
}
```

`ModelImageResponse` exposes the same `Usage`. The MetaModelAPISample scene and the AI stats panel in the Gym scene display these metrics for each request.

### Service Health

`ModelServiceHealth` tracks whether the service is answering at all, across every request the client makes. A single failed request means little — callers retry — so failures are only reported once `FAILURES_BEFORE_DEGRADED` (3) requests in a row have failed, and the next successful request clears the state immediately.

- `ModelServiceHealth.IsDegraded` — true while the service is considered down.
- `ModelServiceHealth.LastFailure` — why the last request failed: `Timeout`, `NoNetwork` or `ServerError` (`ModelRequestOutcome`).
- `ModelServiceHealth.ConsecutiveFailures` — the current run of failed requests.
- `ModelServiceHealth.DegradedChanged` — raised with `(true, outcome)` when the service is reported down and with `(false, Success)` when it answers again.

```csharp
ModelServiceHealth.DegradedChanged += (degraded, outcome) =>
{
    if (!degraded)
    {
        HideServiceError();
    }
    else if (outcome == ModelRequestOutcome.NoNetwork)
    {
        ShowNoNetworkError();
    }
    else
    {
        ShowServerError();
    }
};
```

Spatial Lingo uses this event to show the Wi-Fi or server error icon on the microphone UI.

## AssistantAI Wrapper

[AssistantAI.cs](../../Assets/SpatialLingo/Scripts/AI/AssistantAI.cs) encapsulates the prompts, chats, and data passing, JSON parsing necessary to communicate with the model and receive C# objects.

The class is initialized by passing an instance of the MetaModelRestApi:

```csharp
var assistantAI = new AssistantAI(modelApi);
```

By abstracting the details away, the Assistant AI interface allows for more targeted usage. As an example, word clouds are generated from a single classification & cropped camera image (from an object recognition pass). The result is a rich description of the scene with nouns, verbs, and adjectives.

```csharp
// Definition ([AssistantAI.cs](../../Assets/SpatialLingo/Scripts/AI/AssistantAI.cs)):
public async Task<WordCloudResult> GenerateWordCloudData(SupportedLanguage userLanguage, SupportedLanguage targetLanguage, string classification, string imageString, string context = null)

// Call:
var wordCloudResult = await m_assistantAI.GenerateWordCloudData(userLanguage, targetLanguage, classification, imageString);

// wordCloudResult contains parts of speech for presentation to the user during gameplay
```

To prepare an image for a request, `GetNetworkSafeImageString` scales the texture down to a pixel budget of about 400×300 (never up), optionally blurs any faces in it, and encodes it as a JPEG base64 data URL (`data:image/jpeg;base64,...`) ready to pass as `imageString`. Smaller images keep requests fast and token usage low:

```csharp
// Definition ([AssistantAI.cs](../../Assets/SpatialLingo/Scripts/AI/AssistantAI.cs)):
public static async Awaitable<string> GetNetworkSafeImageString(Texture2D imageSource, FaceDetection faceBlur)

// Call (pass null instead of a FaceDetection to skip face blurring):
var imageString = await AssistantAI.GetNetworkSafeImageString(croppedImage, faceDetection);
```

## Example Use

### Basic Chat Example

```csharp
using Meta.Utilities.MetaModelAPI;

// ...

var api = new MetaModelRestApi();

var chat = api.StartNewChat($"You are a translation assistant. You translate sentences from \"English\" to \"Spanish\".");
var task = api.ContinueChat(chat, $"Translate this sentence from \"English\" to \"Spanish\": \"Where is the nearest coffee shop?\" ");
var response = await task;

var translation = "";
if (response != null)
{
    translation = response.Message.Text;
}

Debug.Log($"Translation: {translation}");
```

## Example Scenes

### MetaModelAPISample Scene

The example Meta Model API scene demonstrates how to make basic calls using the MetaModelRestApi class:

Example Image:

![640_atrium.png](Documentation~/Images/MODELAPI/640_atrium.png)

Example Result:

![ExampleMetaModel.png](Documentation~/Images/MODELAPI/ExampleMetaModel.png)

### AssistantAISample Scene

The example Assistant AI scene demonstrates how to make basic calls using the AssistantAI class:

Example AssistantAI generating sentences with varying degrees of complexity:

![ExampleAssistantAI2.png](Documentation~/Images/MODELAPI/ExampleAssistantAI2.png)

Example AssistantAI evaluating a user transcription, responding with reasoning:

![ExampleAssistantAI1.png](Documentation~/Images/MODELAPI/ExampleAssistantAI1.png)

## Common Use Cases

### Accurate Object Description

The classification from object detection is a coarse (and sometimes incorrect) umbrella term for detected objects. Passing a cropped image of the subject item to the model, the scene description is able to provide a more accurate and descriptive summary of the item.

### Generating Lesson Data (Word Clouds)

Passing a descriptive noun along with an image of the corresponding object to the scene description service, it is able to return a rich set of nouns, adjectives, and verbs.

### Translation

The model is used to translate the scene description results into both the user's native language and the target language, to facilitate translation and language learning.

### Speech Evaluation

Using a transcription of the user's voice, the model is used to evaluate a user's response using multiple criteria, including checking for the presence of specific words and parts of speech (nouns, verbs, and adjectives).
