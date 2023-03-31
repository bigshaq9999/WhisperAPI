using System.Globalization;
using System.Text.Json;
using Serilog;
using WhisperAPI.Models;

namespace WhisperAPI.Services;

public class TranscriptionService : ITranscriptionService
{
    #region Constructor

    private readonly IAudioConversionService _audioConversionService;
    private readonly TranscriptionHelper _transcriptionHelper;
    private readonly Globals _globals;

    public TranscriptionService(Globals globals,
        IAudioConversionService audioConversionService,
        TranscriptionHelper transcriptionHelper)
    {
        _globals = globals;
        _audioConversionService = audioConversionService;
        _transcriptionHelper = transcriptionHelper;
    }

    #endregion

    #region Methods

    public async Task<PostResponse> HandleTranscriptionRequest(IFormFile file, PostRequest request)
    {
        var lang = request.Lang.Trim().ToLower();
        if (lang != "auto")
        {
            if (lang.Length is 2)
                lang = CultureInfo.GetCultures(CultureTypes.AllCultures)
                    .FirstOrDefault(c => c.TwoLetterISOLanguageName == lang)?.EnglishName;

            if (CultureInfo.GetCultures(CultureTypes.AllCultures).All(c => !lang!.Contains(c.EnglishName)))
            {
                Log.Warning("Invalid language: {Lang}", lang);
                return FailResponse(ErrorCodesAndMessages.InvalidLanguage,
                    ErrorCodesAndMessages.InvalidLanguageMessage);
            }

            lang = new CultureInfo(lang!).TwoLetterISOLanguageName;
        }

        if (!Enum.TryParse(request.Model, true, out WhisperModel modelEnum))
        {
            Log.Warning("Invalid model: {Model}", request.Model);
            return FailResponse(ErrorCodesAndMessages.InvalidModel, ErrorCodesAndMessages.InvalidModelMessage);
        }

        // Check if the audio files folder exists, if not create it
        if (!Directory.Exists(_globals.AudioFilesFolder))
            Directory.CreateDirectory(_globals.AudioFilesFolder);

        // Create the files
        var fileId = Guid.NewGuid().ToString();
        var fileExt = Path.GetExtension(file.FileName);
        var filePath = Path.Combine(_globals.AudioFilesFolder, $"{fileId}{fileExt}");
        var wavFilePath = Path.Combine(_globals.AudioFilesFolder, $"{fileId}.wav");
        await using FileStream fs = new(filePath, FileMode.Create);
        await file.CopyToAsync(fs).ConfigureAwait(false);

        var result = await ProcessAudioTranscription(
            filePath,
            wavFilePath,
            lang,
            request.Translate,
            modelEnum,
            request.TimeStamps);

        if (result.transcription is not null)
            return new PostResponse
            {
                Success = true,
                Result = request.TimeStamps
                    ? JsonSerializer.Deserialize<List<TimeStamp>>(result.transcription)
                    : result.transcription
            };

        Log.Warning("Transcription failed: {ErrorCode} - {ErrorMessage}", result.errorCode, result.errorMessage);
        return FailResponse(result.errorCode, result.errorMessage);
    }

    public async Task<(string? transcription, string? errorCode, string? errorMessage)> ProcessAudioTranscription(
        string fileName,
        string wavFile,
        string lang,
        bool translate,
        WhisperModel whisperModel,
        bool timeStamp)
    {
        var selectedModelPath = _globals.ModelFilePaths[whisperModel];
        var selectedOutputFormat = _globals.OutputFormatMapping[timeStamp];

        await _transcriptionHelper.DownloadModelIfNotExists(whisperModel, selectedModelPath);
        await _audioConversionService.ConvertToWavAsync(fileName, wavFile);

        // The CLI Arguments in Whisper.cpp for the output file format are `-o<extension>` so we just parse the extension after the `-o`
        var transcribedFilePath = Path.Combine(_globals.AudioFilesFolder, $"{wavFile}.{selectedOutputFormat[2..]}");
        await _transcriptionHelper.Transcribe(wavFile, lang, translate, selectedModelPath, selectedOutputFormat);

        string[] filesToDelete = { fileName, wavFile, transcribedFilePath };

        if (timeStamp)
        {
            var jsonLines = _transcriptionHelper.ConvertToJson(transcribedFilePath);
            foreach (var file in filesToDelete)
                File.Delete(file);
            var serialized = JsonSerializer.Serialize(jsonLines).Trim();
            return (serialized, null, null);
        }

        var transcribedText = await File.ReadAllTextAsync(transcribedFilePath);
        foreach (var file in filesToDelete)
            File.Delete(file);
        return (transcribedText.Trim(), null, null);
    }

    public PostResponse FailResponse(string? errorCode, string? errorMessage)
    {
        return new PostResponse
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    #endregion
}