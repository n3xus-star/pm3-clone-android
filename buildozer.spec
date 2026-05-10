[app]
title = PM3 Clone Assistant
package.name = pm3clone
package.domain = com.pm3clone

source.dir = .
source.include_exts = py,png,jpg,kv,atlas,so

version = 1.0.0

# Requirements: kivy + kivymd + pyjnius
requirements = python3,kivy==2.3.0,kivymd==1.2.0,pyjnius,android,certifi,charset-normalizer,requests

# Orientation: portrait only (phone-friendly)
orientation = portrait

# Android
android.minapi = 26
android.api = 33
android.ndk = 25b
android.sdk = 33
android.build_tools_version = 33.0.2
android.archs = arm64-v8a

# Permissions
android.permissions = android.permission.INTERNET

# USB Host feature via manifest XML injection
android.extra_manifest_xml = %(source.dir)s/extra_manifest.xml

# USB device filter XML resource (referenced by meta_data below)
android.res_xml = %(source.dir)s/res/xml/device_filter.xml

# USB intent filter: auto-open when PM3 plugged in
android.meta_data = android.hardware.usb.action.USB_DEVICE_ATTACHED=@xml/device_filter

# Extra Java files for USB BroadcastReceiver
# android.add_src = java/

# Icon (optional — add icon.png to android-app/ if desired)
# icon.filename = %(source.dir)s/icon.png

# Splash screen
# presplash.filename = %(source.dir)s/presplash.png

[buildozer]
log_level = 2
warn_on_root = 1

# Build directory
# build_dir = ./.buildozer
# bin_dir = ./bin
