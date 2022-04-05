using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Altinn.Dan.Plugin.Pensjon.Config;
using Altinn.Dan.Plugin.Pensjon.Models;
using Azure.Core.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nadobe;
using Nadobe.Common.Exceptions;
using Nadobe.Common.Models;
using Nadobe.Common.Util;
using Newtonsoft.Json;

namespace Altinn.Dan.Plugin.Pensjon
{
    public class Main
    {
        private ILogger _logger;
        private readonly HttpClient _client;
        private readonly ApplicationSettings _settings;

        public Main(IHttpClientFactory httpClientFactory, IOptions<ApplicationSettings> settings)
        {
            _client = httpClientFactory.CreateClient("ECHttpClient");
            _settings = settings.Value;
        }

        [Function("NorskPensjon")]
        public async Task<HttpResponseData> GetNorskPensjon(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestData req,
            FunctionContext context)
        {
            _logger = context.GetLogger(context.FunctionDefinition.Name);
            _logger.LogInformation("Running func 'NorskPensjon'");
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var evidenceHarvesterRequest = JsonConvert.DeserializeObject<EvidenceHarvesterRequest>(requestBody);

            var actionResult = await EvidenceSourceResponse.CreateResponse(null, () => GetEvidenceValuesPensjon(evidenceHarvesterRequest)) as ObjectResult;
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(actionResult?.Value);

            return response;
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesPensjon(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var content = await MakeRequest<NorskPensjonResponse>(_settings.NorskPensjonUrl, evidenceHarvesterRequest.SubjectParty);

            var ecb = new EvidenceBuilder(new Metadata(), "NorskPensjon");
            ecb.AddEvidenceValue($"default", JsonConvert.SerializeObject(content), Metadata.SOURCE, false);

            return ecb.GetEvidenceValues();
        }

        private async Task<T> MakeRequest<T>(string target, Party subject) where T : new()
        {
            HttpResponseMessage result = null;
            var requestBody = new NorskPensjonRequest();
            requestBody.Fodselsnummer = subject.NorwegianSocialSecurityNumber;
            T response = default(T);

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, target);
                request.Content = new StringContent(requestBody.ToString());
                result = await _client.SendAsync(request);
                switch (result.StatusCode)
                {
                    case HttpStatusCode.OK:
                    {
                        try
                        {
                            response = JsonConvert.DeserializeObject<T>(await result.Content.ReadAsStringAsync());
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Could not deserialize response: {ex.Message}");

                            throw new EvidenceSourcePermanentServerException(Metadata.ERROR_CCR_UPSTREAM_ERROR, "Could not deserialize response: " + ex.Message);
                        }

                        if (response == null)
                        {
                            throw new EvidenceSourcePermanentServerException(Metadata.ERROR_CCR_UPSTREAM_ERROR, "Did not understand the data model returned from upstream source");
                        }

                        return response;
                    }
                    case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    {
                        _logger.LogError($"Authentication failed for Norsk Pensjon for {subject.GetAsString()}");

                        throw new EvidenceSourcePermanentClientException(Metadata.ERROR_ORGANIZATION_NOT_FOUND, $"Authentication failed ({(int)result.StatusCode})");
                    }
                    case HttpStatusCode.InternalServerError:
                    {
                        _logger.LogError($"Call to Norsk Pensjon failed (500 - internal server error)");

                        throw new EvidenceSourceTransientException(Metadata.ERROR_CCR_UPSTREAM_ERROR);
                    }
                    default:
                    {
                        _logger.LogError($"Unexpected status code from external API ({(int)result.StatusCode} - {result.StatusCode})");

                        throw new EvidenceSourcePermanentClientException(Metadata.ERROR_CCR_UPSTREAM_ERROR,
                            $"External API call to Norsk Pensjon failed ({(int)result.StatusCode} - {result.StatusCode})");
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex.Message);

                throw new EvidenceSourcePermanentServerException(Metadata.ERROR_CCR_UPSTREAM_ERROR, null, ex);
            }
        }

        [Function(Constants.EvidenceSourceMetadataFunctionName)]
        public async Task<HttpResponseData> GetMetadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequestData req,
            FunctionContext context)
        {
            _logger = context.GetLogger(context.FunctionDefinition.Name);
            _logger.LogInformation($"Running func metadata for {Constants.EvidenceSourceMetadataFunctionName}");
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new Metadata().GetEvidenceCodes(),
                new NewtonsoftJsonObjectSerializer(new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }));

            return response;
        }
    }
}
