#!/bin/bash

VERSION=$1
NOTES=$2

if [ -z "$VERSION" ] || [ -z "$NOTES" ]; then
  echo "❌ Usage: ./release.sh v1.0.0 \"Release notes here\""
  exit 1
fi

echo "📦 Tagging version: $VERSION"
git tag "$VERSION"
git push origin "$VERSION"

echo "🚀 Creating GitHub release for $VERSION"
gh release create "$VERSION" --title "$VERSION" --notes "$NOTES" --target master

echo "🎉 Release triggered. Check the GitHub Actions tab for progress!"
