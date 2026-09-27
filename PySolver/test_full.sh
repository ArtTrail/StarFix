#!/bin/bash
set -e
useradd -m testuser
mkdir -p /home/testuser/starfix
tar -xzf /build/StarFix-v1.1.1-linux-x64.tar.gz -C /home/testuser/starfix
chown -R testuser:testuser /home/testuser/starfix
su testuser -c "cd /home/testuser/starfix && ./StarFix --solve /build/sparse_test.fits --json" > /dev/null 2>&1 || true
sed -i "s#/home/testuser/.config/StarFix/gaia_catalog#/build/test_catalog#" /home/testuser/.config/StarFix/config.json
echo "--- config.json ---"
cat /home/testuser/.config/StarFix/config.json
echo ""
echo "--- solve result ---"
su testuser -c "cd /home/testuser/starfix && ./StarFix --solve /build/sparse_test.fits --ra 303.147 --dec -2.146 -r 0.5 --json"
