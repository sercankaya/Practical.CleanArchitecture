﻿using ClassifiedAds.CrossCuttingConcerns.Csv;
using ClassifiedAds.CrossCuttingConcerns.HtmlGenerator;
using ClassifiedAds.CrossCuttingConcerns.PdfConverter;
using ClassifiedAds.Services.Product.Authorization;
using ClassifiedAds.Services.Product.Commands;
using ClassifiedAds.Services.Product.DTOs;
using ClassifiedAds.Services.Product.Models;
using ClassifiedAds.Services.Product.Queries;
using ClassifiedAds.Services.Product.RateLimiterPolicies;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClassifiedAds.Services.Product.Controllers;

[EnableRateLimiting(RateLimiterPolicyNames.DefaultPolicy)]
[Authorize]
[Produces("application/json")]
[Route("api/[controller]")]
[ApiController]
public class ProductsController : ControllerBase
{
    private readonly IMediator _dispatcher;
    private readonly ILogger _logger;
    private readonly IHtmlGenerator _htmlGenerator;
    private readonly IPdfConverter _pdfConverter;
    private readonly ICsvWriter<ProductModel> _productCsvWriter;
    private readonly ICsvReader<ProductModel> _productCsvReader;

    public ProductsController(IMediator dispatcher,
        ILogger<ProductsController> logger,
        IHtmlGenerator htmlGenerator,
        IPdfConverter pdfConverter,
        ICsvWriter<ProductModel> productCsvWriter,
        ICsvReader<ProductModel> productCsvReader)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _htmlGenerator = htmlGenerator;
        _pdfConverter = pdfConverter;
        _productCsvWriter = productCsvWriter;
        _productCsvReader = productCsvReader;
    }

    [Authorize(AuthorizationPolicyNames.GetProductsPolicy)]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Entities.Product>>> Get()
    {
        _logger.LogInformation("Getting all products");
        var products = await _dispatcher.Send(new GetProductsQuery());
        var model = products.ToModels();
        return Ok(model);
    }

    [Authorize(AuthorizationPolicyNames.GetProductPolicy)]
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Entities.Product>> Get(Guid id)
    {
        var product = await _dispatcher.Send(new GetProductQuery { Id = id, ThrowNotFoundIfNull = true });
        var model = product.ToModel();
        return Ok(model);
    }

    [Authorize(AuthorizationPolicyNames.AddProductPolicy)]
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<ActionResult<Entities.Product>> Post([FromBody] ProductModel model)
    {
        var product = model.ToEntity();
        await _dispatcher.Send(new AddUpdateProductCommand { Product = product });
        model = product.ToModel();
        return Created($"/api/products/{model.Id}", model);
    }

    [Authorize(AuthorizationPolicyNames.UpdateProductPolicy)]
    [HttpPut("{id}")]
    [Consumes("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Put(Guid id, [FromBody] ProductModel model)
    {
        var product = await _dispatcher.Send(new GetProductQuery { Id = id, ThrowNotFoundIfNull = true });

        product.Code = model.Code;
        product.Name = model.Name;
        product.Description = model.Description;

        await _dispatcher.Send(new AddUpdateProductCommand { Product = product });

        model = product.ToModel();

        return Ok(model);
    }

    [Authorize(AuthorizationPolicyNames.DeleteProductPolicy)]
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(Guid id)
    {
        var product = await _dispatcher.Send(new GetProductQuery { Id = id, ThrowNotFoundIfNull = true });

        await _dispatcher.Send(new DeleteProductCommand { Product = product });

        return Ok();
    }

    [Authorize(AuthorizationPolicyNames.GetProductAuditLogsPolicy)]
    [HttpGet("{id}/auditlogs")]
    public async Task<ActionResult<IEnumerable<AuditLogEntryDTO>>> GetAuditLogs(Guid id)
    {
        var logs = await _dispatcher.Send(new GetAuditEntriesQuery { ObjectId = id.ToString() });

        List<dynamic> entries = new List<dynamic>();
        ProductModel previous = null;
        foreach (var log in logs.OrderBy(x => x.CreatedDateTime))
        {
            var data = JsonSerializer.Deserialize<ProductModel>(log.Log);
            var highLight = new
            {
                Code = previous != null && data.Code != previous.Code,
                Name = previous != null && data.Name != previous.Name,
                Description = previous != null && data.Description != previous.Description,
            };

            var entry = new
            {
                log.Id,
                log.UserName,
                Action = log.Action.Replace("_PRODUCT", string.Empty),
                log.CreatedDateTime,
                data,
                highLight,
            };
            entries.Add(entry);

            previous = data;
        }

        return Ok(entries.OrderByDescending(x => x.CreatedDateTime));
    }

    [HttpGet("exportaspdf")]
    public async Task<IActionResult> ExportAsPdf()
    {
        var products = await _dispatcher.Send(new GetProductsQuery());
        var model = products.ToModels();

        var template = Path.Combine(Environment.CurrentDirectory, $"Templates/ProductList.cshtml");
        var html = await _htmlGenerator.GenerateAsync(template, model);
        var pdf = await _pdfConverter.ConvertAsync(html);

        return File(pdf, MediaTypeNames.Application.Octet, "Products.pdf");
    }

    [HttpGet("exportascsv")]
    public async Task<IActionResult> ExportAsCsv()
    {
        var products = await _dispatcher.Send(new GetProductsQuery());
        var model = products.ToModels();
        using var stream = new MemoryStream();
        _productCsvWriter.Write(model, stream);
        return File(stream.ToArray(), MediaTypeNames.Application.Octet, "Products.csv");
    }

    [HttpPost("importcsv")]
    public IActionResult ImportCsv([FromForm] UploadFileModel model)
    {
        using var stream = model.FormFile.OpenReadStream();
        var products = _productCsvReader.Read(stream);

        // TODO: import to database
        return Ok(products);
    }
}