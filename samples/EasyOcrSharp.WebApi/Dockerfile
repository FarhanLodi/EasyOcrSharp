# Multi-stage build for the EasyOcrSharp web sample.
#
# Build from the REPOSITORY ROOT so the library sources are in context:
#   docker build -f samples/EasyOcrSharp.WebApi/Dockerfile -t easyocrsharp-webapi .

# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so `restore` is cached independently of source edits.
COPY src/EasyOcrSharp/EasyOcrSharp.csproj                     src/EasyOcrSharp/
COPY samples/EasyOcrSharp.WebApi/EasyOcrSharp.WebApi.csproj   samples/EasyOcrSharp.WebApi/
RUN dotnet restore samples/EasyOcrSharp.WebApi/EasyOcrSharp.WebApi.csproj

COPY src/EasyOcrSharp/                   src/EasyOcrSharp/
COPY samples/EasyOcrSharp.WebApi/        samples/EasyOcrSharp.WebApi/
COPY README.md LICENSE ./

RUN dotnet publish samples/EasyOcrSharp.WebApi/EasyOcrSharp.WebApi.csproj \
        -c Release -o /app --no-restore

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Native prerequisites:
#   libgomp1        - ONNX Runtime's CPU kernels are OpenMP-threaded.
#   libfontconfig1  - PDFium (Docnet.Core) resolves fonts through fontconfig when rasterizing.
#   fonts-dejavu-core - gives the searchable-PDF font probe a real TrueType font to embed for Latin,
#                     Greek and Cyrillic. Without ANY installed font the Unicode text layer falls back
#                     to Latin-1 (see PdfOcrResult.TextLayerFontStatus).
#   fonts-noto-cjk  - add this line if you OCR Chinese/Japanese/Korean and want those PDFs searchable.
#                     It is ~120 MB, so it is left out of the default image on purpose.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libgomp1 \
        libfontconfig1 \
        fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Models are downloaded on first use and are large, so cache them on a volume rather than baking them
# into the image or re-fetching them on every container start.
ENV EASYOCRSHARP_CACHE=/models
VOLUME ["/models"]

# Run as the non-root user the aspnet image already provides, and make sure it owns the cache.
RUN mkdir -p /models && chown -R $APP_UID:$APP_UID /models
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Ship the model cache into the image and add `-e EasyOcr__Offline=true` to run with no egress at all.
ENTRYPOINT ["dotnet", "EasyOcrSharp.WebApi.dll"]
