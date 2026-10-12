// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Meta.Utilities.MetaModelAPI;
using Meta.Utilities.ObjectClassifier;
using Meta.XR.Samples;
using UnityEngine;

namespace SpatialLingo.AI
{
    [MetaCodeSample("SpatialLingo")]
    public class AssistantAI
    {
        public enum SupportedLanguage
        {
            English = 0,
            Spanish = 1,
            Vietnamese = 2,
            French = 3,
            German = 4,
            Arabic = 5,
            Italian = 6,
            Thai = 7,
            Hindi = 8,
            Portuguese = 9,
            Indonesian = 10,
            Tagalog = 11,
        }

        public static readonly string[] LanguageEnglishEquivalentName =
        {
            "English",
            "Spanish",
            "Vietnamese",
            "French",
            "German",
            "Arabic",
            "Italian",
            "Thai",
            "Hindi",
            "Portuguese",
            "Indonesian",
            "Tagalog",
        };

        public static readonly string[] LanguageNativeName =
        {
            "English",
            "Español",
            "Tiếng Việt",
            "Français",
            "Deutsch",
            "عربي",
            "Italiana",
            "แบบไทย",
            "हिंदी",
            "Português",
            "Indonesia",
            "Filipino",
        };

        private enum EncodeImageType
        {
            PNG,
            JPG
        }

        public static string SupportedLanguageEnumToEnglishName(SupportedLanguage language)
        {
            return LanguageEnglishEquivalentName[(int)language];
        }

        public static string SupportedLanguageEnumToNativeName(SupportedLanguage language)
        {
            return LanguageNativeName[(int)language];
        }

        private static string[] ListAddQuotesToLower(string[] list)
        {
            if (list == null)
            {
                return null;
            }

            var count = list.Length;
            var newList = new string[count];
            for (var i = 0; i < count; i++)
            {
                var value = list[i];
                value = value.ToLower();
                newList[i] = $"\"{value}\"";
            }

            return newList;
        }

        public enum ImageDescriptionScope
        {
            Short, // concise, short 1-2 sentences
            Medium, // 3-4 sentences
            Long, // descriptive, 5-6 sentences
        }

        public delegate void TranslationCompleteEvent(TranslationResult result);
        public event TranslationCompleteEvent TranslationComplete;

        public delegate void RelatedWordsCompleteEvent(RelatedWordResult result);
        public event RelatedWordsCompleteEvent FindRelatedWordsComplete;

        public delegate void WordListCompleteEvent(WordListResult result);
        public event WordListCompleteEvent TranslateWordListComplete;

        public delegate void ImageSummaryCompleteEvent(ImageSummaryResult result);
        public event ImageSummaryCompleteEvent CollectImageSummaryComplete;

        public delegate void TranscriptEvaluationCompleteEvent(WordCloudTranscriptPhraseResult result);

        /// <summary>
        /// Raised for every spoken answer the model judges, so the lesson debug panel can show
        /// what was sent and how it was read back.
        /// </summary>
        public event TranscriptEvaluationCompleteEvent EvaluateTranscriptionComplete;

        public delegate void DialogueCompleteEvent(DialogueResult result);
        public event DialogueCompleteEvent GenerateDialogueComplete;

        // Some size balancing image quality and network bandwidth, 4:3 ratio
        private const int MAXIMUM_REQUEST_DESCRIPTION_IMAGE_WIDTH = 400;
        private const int MAXIMUM_REQUEST_DESCRIPTION_IMAGE_HEIGHT = 300;

        // Instructor's character description for AI prompts
        private string m_characterPersonality = "Your personality is: fun, bubbly, exiting, and interesting. You speak in the first-person voice to talk about yourself. You use second-person voice to talk to the other interlocutor. You use third-person voice to talk about other people you see in the scene. You use gender-neutral words to describe people or behaviors. You use words at a grade school level that are easy for people to understand. The vocabulary you respond with should be understandable by a wide range of people.";

        private MetaModelRestApi m_modelApi;

        /// <summary>
        /// Whether logs may contain what the player said or the model's reply to it: in the editor
        /// and development builds, or while debug mode is on for testing a release build.
        /// </summary>
        private static bool MayLogUserContent => Debug.isDebugBuild || Debugging.SpatialLingoDebug.Enabled;

        public AssistantAI(MetaModelRestApi modelApi) => m_modelApi = modelApi;

        public static string ContextReferenceOrDefault(string context = null)
        {
            if (context == null)
            {
                var guid = Guid.NewGuid();
                var milliseconds = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                context = $"{guid}_{milliseconds}";
            }

            return context;
        }

        public async Task<TranslationResult> Translate(SupportedLanguage fromLanguage, SupportedLanguage toLanguage, string sentence, string context = null)
        {
            return await TranslateInternal(fromLanguage, toLanguage, sentence, context);
        }

        public async Task<TranslationResult> TranslateInternal(SupportedLanguage fromLanguage, SupportedLanguage toLanguage, string sentence, string context)
        {
            context = ContextReferenceOrDefault(context);
            var languageFrom = SupportedLanguageEnumToEnglishName(fromLanguage);
            var languageTo = SupportedLanguageEnumToEnglishName(toLanguage);
            var sentenceFrom = sentence;
            var request = $"Translate this sentence from \"{languageFrom}\" to \"{languageTo}\": \"{sentenceFrom}\" ";
            var chat = m_modelApi.StartNewChat($"You are a translation assistant. You translate sentences from \"{languageFrom}\" to \"{languageTo}\". Do not use beginning or trailing quotation marks to wrap the response.");
            var task = m_modelApi.ContinueChat(chat, request);
            var response = await task;
            var translation = "";
            if (response != null)
            {
                translation = response.Message.Text;
            }
            var result = new TranslationResult(fromLanguage, toLanguage, sentence, translation, context)
            {
                Stats = response?.Usage,
            };
            TranslationComplete?.Invoke(result);
            return result;
        }

        public async Task<RelatedWordResult> FindRelatedWords(SupportedLanguage targetLanguage, string word, int wordCount = 5, string context = null)
        {
            return await FindRelatedWordsInternal(targetLanguage, word, wordCount, context);
        }

        private async Task<RelatedWordResult> FindRelatedWordsInternal(SupportedLanguage targetLanguage, string word, int wordCount, string context)
        {
            context = ContextReferenceOrDefault(context);

            var languageName = SupportedLanguageEnumToEnglishName(targetLanguage);

            var chat = m_modelApi.StartNewChat(
                $"You are a language assistant. You respond in the language: \"{languageName}\". You keep answers concise and focused. Your responses are only comma separated lists. You don't add any other context to the response. Read the entire request before responding. Do not include any partial responses, only return the final result.");
            var request = $"Generate a list of words related to the \"{languageName}\" word: \"{word}\". Separate the related words into separate lists by the corresponding part of speech: \"adjectives\", \"nouns\", \"verbs\". Each list should include: {wordCount} elements. Sort the results by most related first. Return only a json object with 3 keys (\"Adjectives\", \"Nouns\", \"Verbs\") mapping to their respective list as a json array. Return a correctly formatted json response.";
            var task = m_modelApi.ContinueChat(chat, request);
            var response = await task;

            var related = new RelatedWords();
            try
            {
                related = JsonUtility.FromJson<RelatedWords>(response.Message.Text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"GenerateSpeechExamplesForWordCloud - Unable to parse JSON: {response.Message.Text}\n{e.Message}");
            }

            // The model does not always answer with the JSON that was asked for. When parsing
            // fails the struct keeps its null arrays, so hand out empty ones instead - callers
            // join these straight into display strings and a null takes the whole handler down.
            var result = new RelatedWordResult(
                targetLanguage,
                word,
                related.Nouns ?? Array.Empty<string>(),
                related.Adjectives ?? Array.Empty<string>(),
                related.Verbs ?? Array.Empty<string>(),
                context)
            {
                Stats = response?.Usage,
            };
            FindRelatedWordsComplete?.Invoke(result);
            return result;
        }

        public async Task<WordListResult> TranslateWordList(SupportedLanguage fromLanguage, SupportedLanguage toLanguage, string[] words, string context = null)
        {
            return await TranslateWordListInternal(fromLanguage, toLanguage, words, context);
        }

        private async Task<WordListResult> TranslateWordListInternal(SupportedLanguage fromLanguage, SupportedLanguage toLanguage, string[] words, string context)
        {
            context = ContextReferenceOrDefault(context);

            var languageFromName = SupportedLanguageEnumToEnglishName(fromLanguage);
            var languageToName = SupportedLanguageEnumToEnglishName(toLanguage);
            WordListResult result;
            if (words != null && words.Length > 0)
            {
                var commaSeparatedWordList = string.Join(",", words);

                var chat = m_modelApi.StartNewChat(
                    $"You are a language assistant who translates from \"{fromLanguage}\" into \"{toLanguage}\". You respond in the language: \"{toLanguage}\". You keep answers concise and focused. Your responses are only comma separated lists. You don't add any other context to the response. Read the entire request before responding. Do not include any partial responses, only return the final result.");
                var request = $"Please translate this comma separated list of words from \"{fromLanguage}\" into \"{toLanguage}\": {commaSeparatedWordList}. Return a comma separated list of the same words in \"{toLanguage}\". Preserve the ordering. Do not add any other commentary.";
                var task = m_modelApi.ContinueChat(chat, request);
                var response = await task;

                if (response == null || response.Message == null || string.IsNullOrWhiteSpace(response.Message.Text))
                {
                    Debug.LogWarning($"TranslateWordListInternal: null or empty value found ({response}) for word translation for words: {words.Length}");
                    result = new WordListResult(fromLanguage, toLanguage, words, new string[] { }, context);
                }
                else
                {
                    var wordsTo = response.Message.Text.Split(',');
                    for (var i = 0; i < wordsTo.Length; ++i)
                    {
                        var word = wordsTo[i];
                        wordsTo[i] = word.Trim();
                    }

                    if (wordsTo.Length != words.Length)
                    {
                        Debug.LogWarning($"TranslateWordListInternal: word count mismatch {wordsTo.Length} != {words.Length}");
                    }

                    result = new WordListResult(fromLanguage, toLanguage, words, wordsTo, context);
                }
            }
            else
            {
                result = new WordListResult(fromLanguage, toLanguage, new string[] { }, new string[] { }, context);
            }

            TranslateWordListComplete?.Invoke(result);
            return result;
        }

        public static async Awaitable<string> GetNetworkSafeImageString(Texture2D imageSource, FaceDetection faceBlur)
        {
            var imageSend = await GetNetworkSafeImage(imageSource, MAXIMUM_REQUEST_DESCRIPTION_IMAGE_WIDTH, MAXIMUM_REQUEST_DESCRIPTION_IMAGE_HEIGHT, faceBlur);
            var imageString = GetSourceImageAsString(imageSend);
            return imageString;
        }

        public async Awaitable<ImageSummaryResult> AcquireImageSummary(SupportedLanguage targetLanguage, ImageDescriptionScope scope, Texture2D imageSource, FaceDetection faceBlur, string context = null)
        {
            context = ContextReferenceOrDefault(context);
            // Downscale & encode
            var imageSend = await GetNetworkSafeImage(imageSource, MAXIMUM_REQUEST_DESCRIPTION_IMAGE_WIDTH, MAXIMUM_REQUEST_DESCRIPTION_IMAGE_HEIGHT, faceBlur);
            var imageString = GetSourceImageAsString(imageSend);
            return await AcquireImageSummaryInternal(targetLanguage, scope, imageSend, imageString, context);
        }

        private async Task<ImageSummaryResult> AcquireImageSummaryInternal(SupportedLanguage targetLanguage, ImageDescriptionScope scope, Texture2D imageSource, string imageString, string context = null)
        {
            var languageName = SupportedLanguageEnumToEnglishName(targetLanguage);
            // Generate a prompt based on desired description length
            var prompt = new System.Text.StringBuilder();
            _ = prompt.Append("Generate a description of the scene in the image. Use third person tense as if you are an observer in the setting. Be descriptive while also using as few words as possible. Your responses should be concise. Use gender-neutral words to describe people.");
            _ = prompt.Append(scope switch
            {
                ImageDescriptionScope.Short =>
                    " Your response should be very short, only about 1 or 2 sentences.",
                ImageDescriptionScope.Medium =>
                    " Your response should be short, only up to 4 sentences long. Mention the surroundings and only the most salient features. Use adjectives to further enrich the depiction.",
                ImageDescriptionScope.Long =>
                    " Your response should be no more than a paragraph, and only be as long as it needs to describe the scene elements. The feedback you provide should be rich with details. Use lots of adjectives to describe the different nouns. Use relative positions of objects to describe the locations.",
                _ => "",
            });
            _ = prompt.Append($" Your response should only be in the language \"{languageName}\". Do not use beginning or trailing quotation marks.");

            // Get a description of the scene
            string system = null;
            string[] images = { imageString };
            var response = await m_modelApi.ImageUnderstanding(system, prompt.ToString(), images);
            var summary = "";
            if (response != null)
            {
                summary = response.Message;
            }
            var result = new ImageSummaryResult(targetLanguage, scope, imageSource, imageString, summary, context)
            {
                Stats = response?.Usage,
            };
            CollectImageSummaryComplete?.Invoke(result);
            return result;
        }

        public async Task<DialogueResult> GenerateExploreRoomDialogue(DialogueContextExploreRoom dialogueContext, string context = null)
        {
            context = ContextReferenceOrDefault(context);
            return await GenerateExploreRoomDialogueInternal(dialogueContext, context);
        }

        private async Task<DialogueResult> GenerateExploreRoomDialogueInternal(DialogueContextExploreRoom dialogueContext, string context = null)
        {
            var languageName = SupportedLanguageEnumToEnglishName(dialogueContext.TargetLanguage);
            var systemPrompt = $"You are a helpful language teacher. All your responses are always in the language \"{languageName}\". {m_characterPersonality} You keep your responses short and concise. Your replies are no more than 2 short sentences. You don't use filler words. You want to encourage learning and foster positive outcomes. Use second-person voice to talk with the interlocutor. Only return the words that are said verbally, do not include mannerisms or other physical feedback in your response. ";
            var request = $"Generate a sentence as if you are talking to someone (another interlocutor) and your goal is to get them to look around the room. You want them to explore the area to find objects to talk about. A current description of the scene is: [{dialogueContext.SceneDescription}] Use information from the scene description to communicate effectively to the other interlocutor where to look or where to move. Do not use beginning or trailing quotation marks to wrap the response.";
            var chat = m_modelApi.StartNewChat(systemPrompt);
            var response = await m_modelApi.ContinueChat(chat, request);
            var dialogue = "";
            if (response != null)
            {
                dialogue = response.Message.Text;
            }

            var result = new DialogueResult(dialogueContext.TargetLanguage, dialogue, context)
            {
                Stats = response?.Usage,
            };
            GenerateDialogueComplete?.Invoke(result);
            return result;
        }


        public async Task<DialogueResult> GenerateExampleSentenceDialogue(DialogueContextFocusWord dialogueContext,
            string context = null)
        {
            var languageName = SupportedLanguageEnumToEnglishName(dialogueContext.TargetLanguage);
            var systemPrompt =
                $"You are a helpful language teacher. All your responses are always in the language \"{languageName}\". {m_characterPersonality} You keep your responses short and concise. Your reply should be one very short sentence. You do not use filler words. You want to encourage learning and understanding of the word: \"{dialogueContext.Word}\". Use second-person voice to talk with the interlocutor. Only return the words that are said verbally, do not include mannerisms or other physical feedback in your response.";
            var request =
                $"Generate and suggest an example sentence as if you are talking to someone (another interlocutor) and your goal is to get them to understand the word: \"{dialogueContext.Word}\". The sentence should only be about 4 to 6 words long.";
            if (dialogueContext.RelatedWords != null && dialogueContext.RelatedWords.Length > 0)
            {
                var list = string.Join(",", dialogueContext.RelatedWords);
                request += $"Use the following list of words in your response sentence: {list}.";
            }

            request += " Do not use beginning or trailing quotation marks to wrap the response.";

            var chat = m_modelApi.StartNewChat(systemPrompt);
            var response = await m_modelApi.ContinueChat(chat, request);
            var dialogue = "";
            if (response != null)
            {
                dialogue = response.Message.Text;
            }

            var result = new DialogueResult(dialogueContext.TargetLanguage, dialogue, context)
            {
                Stats = response?.Usage,
            };
            GenerateDialogueComplete?.Invoke(result);
            return result;
        }

        public async Task<WordCloudResult> GenerateWordCloudData(SupportedLanguage userLanguage, SupportedLanguage targetLanguage, string classification, string imageString, string context = null)
        {
            context = ContextReferenceOrDefault(context);

            var languageUser = SupportedLanguageEnumToEnglishName(userLanguage);
            var languageTarget = SupportedLanguageEnumToEnglishName(targetLanguage);
            var isSingleLanguage = userLanguage == targetLanguage;
            var prompt = $"You are a helpful language translation assistant that provides concise answers for the word \"{classification}\".";
            var request = new System.Text.StringBuilder();
            _ = request.Append($"Provide a list of 3 adjectives that can be used to describe the word, and 2 relevant verbs that can be used for the word, and the direct translation for the word: \"{classification}\".");
            _ = isSingleLanguage
                ? request.Append($" Include the \"{languageTarget}\" translations for all words.")
                : request.Append($" Include both the \"{languageUser}\" and matching \"{languageTarget}\" translations for all words.");

            if (userLanguage == SupportedLanguage.Spanish || targetLanguage == SupportedLanguage.Spanish)
            {
                _ = request.Append($" Adjectives in \"{languageTarget}\" should be gendered for the word. Verbs in \"{languageTarget}\" should be in infinitive form. Nouns in \"{languageTarget}\" should always include the gendered article.");
            }

            if (imageString != null)
            {
                _ = request.Append($" Use the provided image to find if there is a more accurate term to use for the word rather than \"{classification}\". If so, use the more accurate word instead of \"{classification}\".");
                _ = request.Append($" Only substitute in a word if you are very confident in the evaluation."); // Substitution Accuracy
                _ = request.Append($" The substituted word term should be only 1 word long and have a focused name."); // Single Word
                _ = request.Append($" The substituted word term should be also only be a single word when translated into \"{languageTarget}\"."); // Target language single word
                _ = request.Append($" The substituted word translated into the language \"{languageTarget}\" should be a standard word in that language."); // Standard words
                _ = request.Append($" Do not include any adjectives in the alternative word choice, only use a noun in the term."); // Exclude Adjectives
                _ = request.Append($" The \"{languageTarget}\" translation of the \"Word\" should not include adjectives either."); // Exclude Adjectives in target language
                _ = request.Append($" Use the provided image as context to find better adjectives and verbs for the word.");
                _ = request.Append($" Do not use the image quality or lighting conditions to guide the description. Avoid words like \"blurry\" and \"dark\" and \"pixelated\". "); // Dark & Blurry
            }

            _ = request.Append($" The first letter should always be uppercase. Do not uppercase all letters.");
            _ = request.Append($" Do not provide multiple alternatives for the same word, pick only a single word for each entry.");

            _ = request.Append($" Format the response in a json formatted object with high level list of objects indexed on the key \"Wordclouds\", which should include an entry for each language.");
            _ = request.Append($" Each object should consist of the keys \"Language\" and \"Adjectives\" and \"Verbs\" and \"Word\". Only include a single best response without any additional commentary.");

            string responseText = null;
            MetaModelRestApi.ModelUsage usage = null;
            if (imageString != null)
            {
                var list = new string[] { imageString };
                var task = m_modelApi.ImageUnderstanding(prompt, request.ToString(), list);
                var response = await task;
                if (response != null)
                {
                    responseText = PrepareJsonStringForParsing(response.Message);
                    usage = response.Usage;
                }
            }
            else
            {
                var chat = m_modelApi.StartNewChat(prompt);
                var task = m_modelApi.ContinueChat(chat, request.ToString());
                var response = await task;
                if (response != null)
                {
                    responseText = PrepareJsonStringForParsing(response.Message.Text);
                    usage = response.Usage;
                }
            }

            WordCloudData cloud = null;
            try
            {
                cloud = responseText != null ? JsonUtility.FromJson<WordCloudData>(responseText) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"GenerateSpeechExamplesForWordCloud - Unable to parse JSON: {responseText}\n{e.Message}");
            }

            var result = new WordCloudResult(userLanguage, targetLanguage, classification, cloud, context)
            {
                Stats = usage,
            };
            return result;
        }

        public static string PrepareJsonStringForParsing(string jsonString)
        {
            jsonString = Regex.Replace(jsonString, @"^```json", "");
            jsonString = Regex.Replace(jsonString, @"^```", "");
            jsonString = Regex.Replace(jsonString, @"```$", "");
            return jsonString;
        }

        public async Task<WordCloudPhraseResult> GenerateSpeechExamplesForWordCloud(SupportedLanguage targetLanguage, string word, string[] verbs, string[] adjectives, string context = null)
        {
            context = ContextReferenceOrDefault(context);

            if (verbs == null || verbs.Length < 2)
            {
                Debug.LogWarning($"AssistantAI - GenerateSpeechExamplesForWordCloud - not enough verbs");
                return new WordCloudPhraseResult(targetLanguage, null, context);
            }
            if (adjectives == null || adjectives.Length < 3)
            {
                Debug.LogWarning($"AssistantAI - GenerateSpeechExamplesForWordCloud - not enough adjectives");
                return new WordCloudPhraseResult(targetLanguage, null, context);
            }
            var languageName = SupportedLanguageEnumToEnglishName(targetLanguage);
            var prompt = $"You are a helpful language translation assistant that provides concise answers for the word \"{word}\", in the target language \"{languageName}\". Your responses are only in JSON formatted data. You do not provide extra commentary. You do not provide optional results, only a single final best result.";
            var request = new System.Text.StringBuilder();
            _ = request.Append($"Provide 2 example sentences in the \"{languageName}\" language with the following criteria:");
            _ = request.Append($" 1 very short phrase including the word: \"{word}\", and one of these adjectives: \"{adjectives[0]}\", \"{adjectives[1]}\".");
            _ = request.Append($" 1 short phrase including the word: \"{word}\", and one of these adjectives: \"{adjectives[0]}\", \"{adjectives[1]}\", \"{adjectives[2]}\", and one of these verbs: \"{verbs[0]}\", \"{verbs[1]}\".");
            _ = request.Append($" All resulting sentences should be grammatically correct for the \"{languageName}\" language. Format the response in a JSON formatted object with high level keys \"Phrase1\" and \"Phrase2\", denoting each example phrase.");
            _ = request.Append($" Only include a single best JSON formatted response without any additional commentary.");
            _ = request.Append($" Do not include alternative JSON results. Include only a JSON formatted response.");

            var chat = m_modelApi.StartNewChat(prompt);
            var task = m_modelApi.ContinueChat(chat, request.ToString());
            var response = await task;
            var responseText = "";
            if (response != null)
            {
                responseText = PrepareJsonStringForParsing(response.Message.Text);
            }

            WordCloudPhraseData phrases = null;
            try
            {
                phrases = response != null ? JsonUtility.FromJson<WordCloudPhraseData>(responseText) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"GenerateSpeechExamplesForWordCloud - Unable to parse JSON: {responseText}\n{e.Message}");
            }
            if (phrases != null)
            {
                phrases.Phrase0 = word;
            }

            var result = new WordCloudPhraseResult(targetLanguage, phrases, context)
            {
                Stats = response?.Usage,
            };
            return result;
        }

        public async Task<WordCloudTranscriptPhraseResult> EvaluateTranscriptionForWordCloud(SupportedLanguage spokenLanguage, SupportedLanguage feedbackLanguage, string transcript, string[] nouns, string[] adjectives, string[] verbs, string context = null)
        {
            context = ContextReferenceOrDefault(context);
            if (nouns == null || nouns.Length == 0)
            {
                return null;
            }

            var spokenLanguageName = SupportedLanguageEnumToEnglishName(spokenLanguage);
            var feedbackLanguageName = SupportedLanguageEnumToEnglishName(feedbackLanguage);
            // Deliberately does not ask the model to answer in the spoken language. That would
            // fight with the request for a fixed verdict and yield the spoken-language word for
            // "yes", which no verdict check can reasonably match. The verdict is JSON; only
            // Reason carries natural language.
            var prompt = $"You are a strict language evaluator. You reply with a single JSON object and nothing else. You never add commentary, explanation or markdown fences.";
            var request = "";
            request += $"A persons speech was transcribed to have said the statement: \"{transcript.ToLower()}\" in the context of the language: {spokenLanguageName}.";

            var hasAdjectives = false;
            var hasVerbs = false;
            var verbList = "";
            var adjList = "";

            var nounList = string.Join(", ", ListAddQuotesToLower(nouns));
            var wordList = nounList;
            request += $" In {spokenLanguageName} is this a correct statement which includes the word: {nounList}";

            if (adjectives != null && adjectives.Length > 0)
            {
                hasAdjectives = true;
                adjList = string.Join(", ", ListAddQuotesToLower(adjectives));
                request += $", and includes one or more of these adjectives: {adjList}";
                wordList = wordList + ", " + adjList;
            }

            if (verbs != null && verbs.Length > 0)
            {
                hasVerbs = true;
                verbList = string.Join(", ", ListAddQuotesToLower(verbs));
                request += $", and includes one or more of these verbs: {verbList}";
                wordList = wordList + ", " + verbList;
            }

            request += "?";
            if (hasVerbs & hasAdjectives)
            {
                request += $" The word: {nounList} must be present, and one or more of the listed adjectives: {adjList} must be present, and one or more of the listed verbs: {verbList} must be present in the statement to be acceptable. Otherwise it is not acceptable.";
            }
            else if (hasVerbs)
            {
                request += $" The word: {nounList} must be present, and one or more of the listed verbs: {verbList} must be present in the statement to be acceptable. Otherwise it is not acceptable.";
            }
            else if (hasAdjectives)
            {
                request += $" The word: {nounList} must be present, and one or more of the listed adjectives: {adjList} must be present in the statement to be acceptable. Otherwise it is not acceptable.";
            }
            else
            {
                request += $" The word: {nounList} must be present for the statement to be acceptable.";
            }

            if (spokenLanguage == SupportedLanguage.Spanish)
            {
                request += " Differences in verb conjugation is acceptable.";
                request += " Differences in adjective gender is acceptable.";
                request += " Differences in articles used is acceptable.";
                request += " Missing articles is acceptable.";
            }
            request += " Do not criticize nonstandard words, nonstandard words are allowed.";
            request += " Unusual or uncommon words are allowed for correctness.";
            request += $" Do not criticize any word in the list: ({wordList}), these are valid words and each is accepted as correct.";
            request += " Differences in letter casing is acceptable. Differences in capitalization is acceptable. Differences in punctuation is acceptable. Differences in spacing is acceptable.";
            request += " Separating or combining compound words (with a space or hyphen) is acceptable.";

            request += " Transcribed words in the user statement that are very phonetically similar to the expected words should be interpreted as the correct word and not marked as needing correction.";

            // A fixed JSON shape rather than a yes/no word, because a word answer comes back in
            // whatever language the reply is written in. A "Pass" boolean is language independent.
            request += " Respond with a single JSON object and nothing else, with exactly two keys:";
            request += " \"Pass\" (a JSON boolean, true if the statement is acceptable, false otherwise)";
            request += $" and \"Reason\" (a string). When \"Pass\" is true, \"Reason\" must be an empty string. When \"Pass\" is false, \"Reason\" must be one very short sentence written in \"{feedbackLanguageName}\" explaining why the speech is not acceptable.";
            request += " Do not repeat the input statement in the reason. Do not wrap the JSON in markdown fences. Do not add any text before or after the JSON object.";

            // Set system prompt
            var chat = m_modelApi.StartNewChat(prompt);

            // Insert history for mistranscribed words
            _ = m_modelApi.AddChatHistoryAsUser(chat, "Sometimes words in the user statement are mistranscribed, like \"La Silla\" may be mistranscribed as \"La CIA\"." +
                                                  " If you evaluate the user statement as incorrect, please re-evaluate considering phonetically similar words may be mistranscriptions." +
                                                  " The intended user word may not have been transcribed correctly, but that should not prevent the entire statement to be incorrect in this context." +
                                                  " This advice should be generalized for all transcribed words, not just the example given here.");
            _ = m_modelApi.AddChatHistoryAsAI(chat, "Understood: Example words like \"La Silla\" may be mistranscribed but because of phonetic similarity, if the example phrase \"La CIA\" is found:" +
                                                " then the user intent to say \"La Silla\" should be substituted in the user statement for \"La CIA\". " +
                                                " This advice should apply generally for all words in the user statement that are phonetically similar to the target words.");

            if (hasVerbs & hasAdjectives)
            {
                _ = m_modelApi.AddChatHistoryAsUser(chat, "The user statement must have at least one word from each of the following lists to be accepted as correct:\n" +
                                                      $" nouns: {nounList}\n adjectives: {adjList}\n verbs: {verbList}");
                _ = m_modelApi.AddChatHistoryAsAI(chat, "Understood.");

            }



            var task = m_modelApi.ContinueChat(chat, request);
            var response = await task;
            var answer = false;
            var reason = "";
            if (response != null)
            {
                ParseTranscriptEvaluation(response.Message.Text, out answer, out reason);
            }

            // One line per evaluation, so a rejected answer can be traced from logcat: what was
            // heard, what the model replied (escaped so its line breaks show), and the verdict.
            // It contains the player's speech, so it is only written while testing.
            if (MayLogUserContent)
            {
                var rawReply = response?.Message?.Text?.Replace("\r", "\\r").Replace("\n", "\\n") ?? "<no response>";
                Debug.Log($"AssistantAI - EvaluateTranscriptionForWordCloud ({spokenLanguageName}): heard \"{transcript}\" | reply: {rawReply} | pass={answer} reason=\"{reason}\"");
            }

            var result = new WordCloudTranscriptPhraseResult(spokenLanguage, transcript, nouns, adjectives, verbs, answer, reason, context)
            {
                Stats = response?.Usage,
                HasVerdict = response != null,
            };
            EvaluateTranscriptionComplete?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Affirmatives for the fallback path, in every language the app supports. Only consulted
        /// when the reply has no JSON verdict - a correct answer must not be rejected just because
        /// the shape was wrong. Matched against the reply's first word, so "Yes, that's right"
        /// passes and "No, I cannot say yes" does not.
        /// </summary>
        private static readonly string[] s_affirmativeWords =
        {
            "yes", "yeah", "yep", "true",
            "sí", "sì",          // Spanish, Italian
            "oui",               // French
            "ja",                // German
            "sim",               // Portuguese
            "có", "vâng",        // Vietnamese
            "نعم", "naam",       // Arabic
            "ใช่",               // Thai
            "हाँ", "haan",       // Hindi
            "iya",               // Indonesian
            "oo", "opo",         // Tagalog
        };

        /// <summary>
        /// Affirmatives that are also ordinary words elsewhere - unaccented "si" is Spanish "if"
        /// and the Italian pronoun, "ya" is Spanish "already". Only accepted when they are the
        /// whole first line, never as the opening word of a sentence.
        /// </summary>
        private static readonly string[] s_standaloneAffirmativeWords = { "si", "ya", "co" };

        /// <summary>
        /// Whether a reply's first line opens with a yes. Thai is written without spaces between
        /// words, so its affirmative may run straight into the next word ("ใช่ครับ").
        /// </summary>
        private static bool StartsWithAffirmative(string firstLine)
        {
            var line = firstLine.ToLowerInvariant();
            var start = 0;
            while (start < line.Length && !IsWordCharacter(line[start]))
            {
                start++;
            }

            var end = start;
            while (end < line.Length && IsWordCharacter(line[end]))
            {
                end++;
            }

            var word = line[start..end];
            if (word.Length == 0)
            {
                return false;
            }

            foreach (var affirmative in s_affirmativeWords)
            {
                if (word == affirmative || (affirmative == "ใช่" && word.StartsWith(affirmative, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            var restHasWords = false;
            for (var i = end; i < line.Length && !restHasWords; i++)
            {
                restHasWords = IsWordCharacter(line[i]);
            }

            return !restHasWords && Array.IndexOf(s_standaloneAffirmativeWords, word) >= 0;

            // Combining marks are part of the word in Hindi and Thai ("हाँ", "ใช่").
            static bool IsWordCharacter(char c) => char.IsLetter(c) ||
                char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;
        }

        /// <summary>
        /// Read the verdict out of an evaluation response.
        ///
        /// The model is asked for {"Pass": bool, "Reason": string}. If that cannot be parsed the
        /// fallback looks for an affirmative token, because the alternative - defaulting to
        /// "failed" - tells the user they got it wrong when the model may well have said yes.
        /// </summary>
        private static void ParseTranscriptEvaluation(string responseText, out bool pass, out string reason)
        {
            pass = false;
            reason = "";

            if (string.IsNullOrWhiteSpace(responseText))
            {
                return;
            }

            // Trimmed before the fences are stripped: the fence patterns are anchored, so leading
            // whitespace would leave the fence in place.
            var cleaned = PrepareJsonStringForParsing(responseText.Trim()).Trim();

            // The verdict is read from the "Pass" key wherever the object sits. JsonUtility alone
            // is not enough: it is case sensitive, never returns null for valid JSON, and throws
            // on text around the object, so {"pass": true}, {} or a short preamble would all have
            // read as a fail without ever reaching the fallback.
            var json = ExtractJsonObject(cleaned);
            if (json != null && TryReadVerdict(json, out pass, out reason))
            {
                return;
            }

            // Fallback: the model answered in prose. Judge the first non-empty line only, so a
            // "no" whose explanation happens to contain "yes" is not read as a pass.
            Debug.LogWarning(MayLogUserContent
                ? $"EvaluateTranscriptionForWordCloud - verdict was not JSON, falling back to text: \"{cleaned}\""
                : "EvaluateTranscriptionForWordCloud - verdict was not JSON, falling back to text");
            var lines = cleaned.Split('\n');
            var firstLine = "";
            var firstLineIndex = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    firstLine = lines[i].Trim();
                    firstLineIndex = i;
                    break;
                }
            }

            if (StartsWithAffirmative(firstLine))
            {
                pass = true;
                return;
            }

            // Everything after the verdict line is the explanation, blank lines and all. Joining
            // the remainder keeps a reason that starts further down rather than only the line
            // immediately after the verdict.
            if (firstLineIndex >= 0 && firstLineIndex + 1 < lines.Length)
            {
                reason = string.Join(" ", lines, firstLineIndex + 1, lines.Length - firstLineIndex - 1).Trim();
            }
        }

        private static readonly Regex s_verdictPassKey = new(@"""pass""\s*:\s*""?(true|false)""?", RegexOptions.IgnoreCase);
        private static readonly Regex s_verdictReasonKey = new(@"""reason""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);

        /// <summary>The text from the first '{' to the last '}', or null when there is no object.</summary>
        private static string ExtractJsonObject(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text[start..(end + 1)] : null;
        }

        /// <summary>
        /// Read "Pass" and "Reason" from a verdict object, in any letter case and alongside any
        /// other keys. False when there is no "Pass" key, so the caller falls back to prose.
        /// </summary>
        private static bool TryReadVerdict(string json, out bool pass, out string reason)
        {
            pass = false;
            reason = "";
            var passMatch = s_verdictPassKey.Match(json);
            if (!passMatch.Success)
            {
                return false;
            }

            pass = string.Equals(passMatch.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            var reasonMatch = s_verdictReasonKey.Match(json);
            if (reasonMatch.Success)
            {
                try
                {
                    reason = Regex.Unescape(reasonMatch.Groups[1].Value);
                }
                catch (ArgumentException)
                {
                    // A malformed escape: show the reason as written rather than none at all.
                    reason = reasonMatch.Groups[1].Value;
                }
            }

            return true;
        }

        /// <summary>
        /// Scale the image down where necessary to limit data sent on network
        /// </summary>
        /// <param name="original">source image</param>
        /// <returns>possibly resized image at a network-useable resolution</returns>
        private static async Awaitable<Texture2D> GetNetworkSafeImage(Texture2D original, int desiredWidth, int desisiredHeight, FaceDetection faceBlur)
        {
            // Minimum image size to still have enough resolution for image understanding (4:3 ratio)
            var desiredTextureSize = new Vector2Int(desiredWidth, desisiredHeight);
            var desiredPixels = desiredTextureSize.x * desiredTextureSize.y;
            // Create a texture
            var tempTexture = original;
            // Determine the output size, rounded to nearest dimensions.
            var inputSize = new Vector2Int(tempTexture.width, tempTexture.height);
            var inputWidthToHeightRatio = inputSize.x / (float)inputSize.y;
            var outputHeight = (int)Mathf.Round(Mathf.Sqrt(desiredPixels / inputWidthToHeightRatio));
            var outputWidth = (int)Mathf.Round(inputWidthToHeightRatio * outputHeight);
            // Only scale down, not up.
            if (outputWidth > original.width)
            {
                outputWidth = original.width;
                outputHeight = original.height;
            }

            try
            {
                tempTexture = Resize(tempTexture, outputWidth, outputHeight);
            }
            catch (Exception)
            {
                Debug.LogWarning($"Could not resize image: {tempTexture.width}x{tempTexture.height} to  {outputWidth}x{outputHeight}");
            }

            if (faceBlur != null)
            {
                faceBlur.InputTexture = tempTexture;
                var blurredTexture = await faceBlur.RunBlurring();

                // Graphics.CopyTexture(blurredTexture, tempTexture);

                RenderTexture.active = blurredTexture;
                tempTexture.ReadPixels(new(0, 0, tempTexture.width, tempTexture.height), 0, 0);
                tempTexture.Apply();
            }
            return tempTexture;
        }

        /// <summary>
        /// Convert the image to a base64 string.
        /// </summary>
        /// <param name="textureImage">source image</param>
        /// /// <param name="encodeType">Encode type, defaults to JPG</param>
        /// <returns>image as a base64 string</returns>
        private static string GetSourceImageAsString(Texture2D textureImage, EncodeImageType encodeType = EncodeImageType.JPG)
        {
            // Get the image as a base64 image string, with corresponding prefix
            string url;
            if (encodeType == EncodeImageType.PNG)
            {
                var base64Data = textureImage.EncodeToPNG();
                var base64String = Convert.ToBase64String(base64Data);
                var prefix = "data:image/png;base64,";
                url = $"{prefix}{base64String}";
            }
            else
            {
                var quality = 90; // default is 75
                var base64Data = textureImage.EncodeToJPG(quality);
                var base64String = Convert.ToBase64String(base64Data);
                var prefix = "data:image/jpeg;base64,";
                url = $"{prefix}{base64String}";
            }

            return url;
        }

        /// <summary>
        /// Resize in image to target dimensions
        /// </summary>
        /// <param name="texture2D">source image</param>
        /// <param name="targetWidth">desired width</param>
        /// <param name="targetHeight">desired height</param>
        /// <returns></returns>
        private static Texture2D Resize(Texture2D texture2D, int targetWidth, int targetHeight)
        {
            // Create temporary render texture
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture.active = renderTexture;
            Graphics.Blit(texture2D, renderTexture);
            // Create a new Texture2D to store the resized result
            var result = new Texture2D(targetWidth, targetHeight, TextureFormat.ARGB32, false);
            // Read the pixel values from the RenderTexture to the new Texture2D
            result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            result.Apply();
            // Release temporary
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(renderTexture);
            return result;
        }
    }
}
