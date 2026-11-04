use gtk::{gio, prelude::*};
use serde_json::{Map, Value, json};
use std::path::Path;
use tinyclips_automation::ApiError;

#[derive(Clone)]
pub struct Preferences(gio::Settings);

const WRITABLE: [(&str, &str); 4] = [
    ("recordMicrophone", "record-microphone"),
    ("recordAudio", "record-audio"),
    ("videoCountdownEnabled", "video-countdown-enabled"),
    ("videoCountdownDuration", "video-countdown-duration"),
];

impl Preferences {
    pub fn load() -> Result<Self, String> {
        let schema = gio::SettingsSchemaSource::default()
            .and_then(|source| source.lookup(crate::APP_ID, true))
            .ok_or("Tiny Clips settings schema is missing. Reinstall the Flatpak.")?;
        Ok(Self(gio::Settings::new_full(
            &schema,
            None::<&gio::SettingsBackend>,
            None,
        )))
    }

    pub fn bind<O: glib::object::IsA<glib::Object>>(&self, key: &str, widget: &O, property: &str) {
        self.0.bind(key, widget, property).build();
    }

    pub fn microphone(&self) -> bool {
        self.0.boolean("record-microphone")
    }
    pub fn system_audio(&self) -> bool {
        self.0.boolean("record-audio")
    }
    pub fn countdown(&self) -> u32 {
        if self.0.boolean("video-countdown-enabled") {
            self.0.int("video-countdown-duration") as u32
        } else {
            0
        }
    }

    pub fn get(
        &self,
        params: &Value,
        screenshots: &Path,
        videos: &Path,
    ) -> Result<Value, ApiError> {
        let writable: Vec<_> = WRITABLE.iter().map(|(name, _)| *name).collect();
        let mut values = Map::new();
        for (name, key) in WRITABLE {
            let value = if name == "videoCountdownDuration" {
                json!(self.0.int(key))
            } else {
                json!(self.0.boolean(key))
            };
            values.insert(name.into(), value);
        }
        values.insert("screenshotFolder".into(), json!(screenshots));
        values.insert("videoFolder".into(), json!(videos));
        values.insert("screenshotFormat".into(), json!("png"));
        if let Some(key) = params.get("key") {
            let key = key
                .as_str()
                .ok_or_else(|| invalid("key must be a string."))?;
            let value = values
                .get(key)
                .ok_or_else(|| invalid("Unknown setting. List settings.get for supported keys."))?;
            return Ok(json!({"key":key,"value":value,"writable":writable.contains(&key)}));
        }
        Ok(json!({"settings":values,"writable":writable}))
    }

    pub fn set(&self, params: &Value) -> Result<Value, ApiError> {
        let name = params["key"]
            .as_str()
            .ok_or_else(|| invalid("A setting key is required."))?;
        let (_, key) = WRITABLE
            .iter()
            .find(|(n, _)| *n == name)
            .ok_or_else(|| invalid("Unknown or read-only setting."))?;
        let value = &params["value"];
        let result = if name == "videoCountdownDuration" {
            let seconds = value
                .as_i64()
                .filter(|s| (1..=10).contains(s))
                .ok_or_else(|| invalid("Countdown must be 1–10 whole seconds."))?;
            self.0.set_int(key, seconds as i32)
        } else {
            self.0.set_boolean(
                key,
                value
                    .as_bool()
                    .ok_or_else(|| invalid("This setting requires a boolean."))?,
            )
        };
        result.map_err(|_| ApiError::new("internal", "Could not save the setting."))?;
        Ok(json!({"key":name,"value":value,"writable":true}))
    }
}

fn invalid(message: &str) -> ApiError {
    ApiError::new("invalid_params", message.to_owned())
}

use gtk::glib;
