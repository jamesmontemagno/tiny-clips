use crate::{
    ClipError, ClipKind, ClipStore, Region, Result,
    annotation::{parse_marks, render},
    copy_clip, open_editor, reveal_clip,
};
use gtk::prelude::*;
use serde_json::{Value, json};
use std::path::Path;

pub const COMMANDS: &[&str] = &[
    "library.list",
    "library.reveal",
    "library.copy",
    "editor.open",
    "editor.annotate",
    "editor.crop",
];
fn fields(params: &Value, keys: &[&str]) -> Result<()> {
    if params
        .as_object()
        .is_none_or(|o| o.keys().any(|k| !keys.contains(&k.as_str())))
    {
        Err(ClipError::invalid(
            "Expected an object with the command's documented parameters.",
        ))
    } else {
        Ok(())
    }
}
fn path(p: &Value) -> Result<&Path> {
    p["path"]
        .as_str()
        .filter(|s| !s.is_empty() && !s.contains('\0'))
        .map(Path::new)
        .ok_or_else(|| ClipError::invalid("Expected a local capture path."))
}

pub(crate) fn dispatch_files(store: &ClipStore, command: &str, params: &Value) -> Result<Value> {
    match command {
        "library.list" => {
            fields(params, &["kind", "limit"])?;
            let limit = params
                .get("limit")
                .map(|n| {
                    n.as_u64()
                        .and_then(|n| usize::try_from(n).ok())
                        .filter(|n| (1..=500).contains(n))
                        .ok_or_else(|| {
                            ClipError::invalid("limit must be an integer from 1 to 500.")
                        })
                })
                .transpose()?
                .unwrap_or(20);
            let kind = match params.get("kind").map(Value::as_str) {
                None | Some(Some("all")) => None,
                Some(Some("screenshot")) => Some(ClipKind::Screenshot),
                Some(Some("video")) => Some(ClipKind::Video),
                Some(Some("gif")) => return Ok(json!({"items":[]})),
                _ => return Err(ClipError::invalid("Unknown library kind.")),
            };
            let items: Vec<_> = store
                .list(kind, limit)?
                .iter()
                .map(|c| {
                    json!({
                        "path": c.path, "name": c.name(), "kind": c.kind.name(),
                        "createdAt": c.created_at(), "sizeBytes": c.size_bytes
                    })
                })
                .collect();
            Ok(json!({"items":items}))
        }
        "editor.annotate" => {
            fields(params, &["path", "annotations"])?;
            let source = path(params)?;
            let image = store.load_png(source)?;
            let marks = parse_marks(&params["annotations"], image.width(), image.height())?;
            let output = store.save_png(&render(&image, &marks)?)?;
            Ok(json!({"path":output,"sourcePath":source,"annotationsApplied":marks.len()}))
        }
        "editor.crop" => {
            fields(params, &["path", "region"])?;
            let source = path(params)?;
            let r: Region = serde_json::from_value(params["region"].clone()).map_err(|_| {
                ClipError::invalid("region must contain integer x, y, width, and height.")
            })?;
            let image = r.crop(&store.load_png(source)?)?;
            let output = store.save_png(&image)?;
            Ok(json!({"path":output,"sourcePath":source,"width":r.width,"height":r.height}))
        }
        _ => Err(ClipError::new(
            "not_supported",
            "Not a local file operation.",
        )),
    }
}

/// Dispatch on the GTK main context *after* the host has paired and checked the command scope.
pub async fn dispatch(
    store: &ClipStore,
    parent: &impl IsA<gtk::Window>,
    command: &str,
    params: &Value,
) -> Result<Value> {
    match command {
        "library.reveal" => {
            fields(params, &["path"])?;
            reveal_clip(store, parent, path(params)?).await?;
            Ok(json!({"revealed":true}))
        }
        "library.copy" => {
            fields(params, &["path"])?;
            copy_clip(store, parent, path(params)?)?;
            Ok(json!({"copied":true}))
        }
        "editor.open" => {
            fields(params, &["path"])?;
            let path = path(params)?;
            if store.resolve(path)?.1.kind != ClipKind::Screenshot {
                return Err(ClipError::new(
                    "not_supported",
                    "Linux edits PNG screenshots only.",
                ));
            }
            open_editor(parent, store, path)?;
            Ok(json!({"opened":true,"editor":"screenshot"}))
        }
        "editor.crop" | "editor.annotate" | "library.list" => {
            let (store, cmd, p) = (store.clone(), command.to_owned(), params.clone());
            gtk::gio::spawn_blocking(move || dispatch_files(&store, &cmd, &p))
                .await
                .map_err(|_| ClipError::internal("Local clip worker failed."))?
        }
        _ => Err(ClipError::new(
            "not_supported",
            "This command is not available in the local clips library.",
        )),
    }
}
