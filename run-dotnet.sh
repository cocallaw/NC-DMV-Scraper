#!/bin/bash

# Build and run script for .NET Core DMV Scraper

set -e

echo "🔧 Building .NET Core DMV Scraper..."

# Check if .NET 8.0 is installed
if ! command -v dotnet &> /dev/null; then
    echo "❌ .NET 8.0 SDK is required but not installed."
    echo "   Please install from: https://dotnet.microsoft.com/download"
    exit 1
fi

# Restore dependencies
echo "📦 Restoring dependencies..."
dotnet restore

# Build the project
echo "🔨 Building project..."
dotnet build --configuration Release

# Check if configuration is set up
if [ ! -f "appsettings.json" ]; then
    echo "❌ appsettings.json not found"
    exit 1
fi

echo "✅ Build completed successfully!"
echo ""
echo "🚀 Starting NC DMV Scraper (.NET Core)..."
echo ""

# Run the application
dotnet run --configuration Release