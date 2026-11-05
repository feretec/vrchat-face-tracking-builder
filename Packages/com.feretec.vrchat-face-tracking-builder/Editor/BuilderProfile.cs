using System;
using System.Collections.Generic;
using UnityEngine;

namespace FaceTrackingBuilder
{
    [Serializable] public class ShapeSource { public string name; public float coefficient = 1; }
    public enum GenerateMode { Combine, SplitLeft, SplitRight, Extract, Landmarks, Mirror }
    [Serializable] public class Landmark
    {
        public string name = "口角";
        public Vector3 position;
        public Vector3 movement;
        public float radius = .025f;
    }
    [Serializable] public class ShapeRecipe
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "FTB_NewExpression";
        public string trackingKey;
        public string mirrorOf;
        public GenerateMode mode;
        public List<ShapeSource> sources = new List<ShapeSource>();
        public List<float> frames = new List<float> { 100 };
        public Vector3 planeOrigin;
        // Mesh-space normal points toward the avatar's anatomical right side.
        public Vector3 planeRight = Vector3.right;
        public float feather = .005f;
        public Vector3 regionCenter;
        public float regionRadius = .04f;
        public float mirrorTolerance = .001f;
        public List<Landmark> landmarks = new List<Landmark>();
        public Vector3[] sculpt = Array.Empty<Vector3>();
        public float[] mask = Array.Empty<float>();
        public bool recalculateSurface;
    }
    [Serializable] public class RendererPlan
    {
        public string path;
        public Mesh source;
        public string fingerprint;
        public bool selected;
        public string evidence;
        public List<ShapeRecipe> recipes = new List<ShapeRecipe>();
    }
    [Serializable] public class TrackingMapping
    {
        public string key;
        public string shape;
        public float multiplier = 1;
        public float confidence;
        public bool confirmed;
        public bool excluded;
        public bool Included => !excluded && !string.IsNullOrEmpty(shape);
        public string reason;
    }
    public class BuilderProfile : ScriptableObject
    {
        public GameObject sourceAvatar;
        public string sourceName;
        public string setName = "FaceTrackingBuilder";
        public List<RendererPlan> renderers = new List<RendererPlan>();
        public List<TrackingMapping> mappings = new List<TrackingMapping>();
        public bool useAllKeys = true;
        public bool routeVisemes;
        public bool routeEyelids;
        public bool routeGaze;
        public bool configureDampening;
        public VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.VisemeDampenMode dampenMode;
        public float dampenAmount = .5f;
        public string lastExport;
        public string analysisSummary;
    }
}
